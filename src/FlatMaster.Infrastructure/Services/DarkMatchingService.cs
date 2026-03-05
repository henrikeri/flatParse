// Copyright (C) 2026 Henrik E. Riise
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Globalization;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Matches dark frames to flat exposure groups
/// </summary>
public sealed class DarkMatchingService(ILogger<DarkMatchingService> logger) : IDarkMatchingService
{
    private const double ExactExposureTolerance = 0.001;
    private const double NearTwoSeconds = 2.0;
    private const double NearTenSeconds = 10.0;
    private readonly ILogger<DarkMatchingService> _logger = logger;

    public DarkMatchResult? FindBestDark(
        ExposureGroup exposureGroup,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options)
    {
        var exposure = exposureGroup.ExposureTime;
        var criteria = exposureGroup.MatchingCriteria ?? new MatchingCriteria();
        var darks = darkCatalog.ToList();

        if (!string.IsNullOrWhiteSpace(criteria.ManualDarkPath))
        {
            var overrideDark = darks.FirstOrDefault(d =>
                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(criteria.ManualDarkPath), StringComparison.OrdinalIgnoreCase));
            if (overrideDark != null)
            {
                _logger.LogInformation("Manual dark override selected: {Path}", overrideDark.FilePath);
                return BuildMatch(overrideDark, optimizeRequired: false, $"Override {ToMatchLabel(overrideDark.Type)}", criteria, options);
            }
        }

        var darkCalibrationCandidates = darks
            .Where(d => IsDarkCalibrationType(d.Type))
            .ToList();

        var strictCandidates = darkCalibrationCandidates
            .Where(d => IsTemperatureAllowed(d, criteria, options.PreferClosestTemp, options.MaxTempDeltaC))
            .ToList();
        var strictMatch = TryFindDarkMatchByPriority(exposure, criteria, options, strictCandidates, tempContextSuffix: null);
        if (strictMatch != null)
            return strictMatch;

        // Before falling back to bias, allow darks to beat bias when delta thresholds are within limits.
        var hasBiasCandidates = darks.Any(d => d.Type is ImageType.MasterBias or ImageType.Bias);
        if (options.DarkOverBiasTempDeltaC > 0 || options.DarkOverBiasExposureDeltaSeconds > 0)
        {
            var anyTempMatch = TryFindDarkMatchByPriority(
                exposure,
                criteria,
                options,
                darkCalibrationCandidates,
                tempContextSuffix: null);

            if (anyTempMatch != null)
            {
                var matchedDark = darks.FirstOrDefault(d =>
                    string.Equals(d.FilePath, anyTempMatch.FilePath, StringComparison.OrdinalIgnoreCase));
                var tempDelta = matchedDark?.Temperature.HasValue == true && criteria.Temperature.HasValue
                    ? Math.Abs(matchedDark.Temperature!.Value - criteria.Temperature.Value)
                    : (double?)null;
                var exposureDelta = matchedDark is not null
                    ? Math.Abs(matchedDark.ExposureTime - exposure)
                    : (double?)null;

                var tempWithinThreshold = options.DarkOverBiasTempDeltaC <= 0
                    || !tempDelta.HasValue
                    || tempDelta.Value <= options.DarkOverBiasTempDeltaC;
                var exposureWithinThreshold = options.DarkOverBiasExposureDeltaSeconds <= 0
                    || !exposureDelta.HasValue
                    || exposureDelta.Value <= options.DarkOverBiasExposureDeltaSeconds;

                if (!hasBiasCandidates || (tempWithinThreshold && exposureWithinThreshold))
                {
                    return anyTempMatch with
                    {
                        MatchKind = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0},temp<={1:0.#}C,exp<={2:0.###}s",
                            anyTempMatch.MatchKind,
                            options.DarkOverBiasTempDeltaC,
                            options.DarkOverBiasExposureDeltaSeconds)
                    };
                }
            }
        }

        // Priority 7: Bias fallback.
        var masterBiasCandidates = darks.Where(d => d.Type is ImageType.MasterBias).ToList();
        if (masterBiasCandidates.Count > 0)
        {
            var best = SelectBestByScore(masterBiasCandidates, criteria, options);
            return BuildMatch(best, optimizeRequired: false, "P7 MasterBias", criteria, options);
        }

        var biasCandidates = darks.Where(d => d.Type is ImageType.Bias).ToList();
        if (biasCandidates.Count > 0)
        {
            var best = SelectBestByScore(biasCandidates, criteria, options);
            return BuildMatch(best, optimizeRequired: false, "P7 Bias", criteria, options);
        }

        // Priority 8: no match.
        _logger.LogWarning("P8: no suitable dark or bias found for exposure {Exposure}s", exposure);
        return null;
    }

    private DarkMatchResult? TryFindDarkMatchByPriority(
        double exposure,
        MatchingCriteria criteria,
        DarkMatchingOptions options,
        List<DarkFrame> darkCandidates,
        string? tempContextSuffix)
    {
        // Priority 1: Master dark exact exposure.
        var p1 = darkCandidates
            .Where(d => IsMasterDarkType(d.Type) && Math.Abs(d.ExposureTime - exposure) < ExactExposureTolerance)
            .ToList();
        if (p1.Count > 0)
        {
            var best = SelectBestByScore(p1, criteria, options);
            _logger.LogInformation("P1 selected exact master dark: {Path} (type={Type}, exp={Exp:F3}s)", best.FilePath, best.Type, best.ExposureTime);
            return BuildMatch(best, optimizeRequired: false, $"P1 {ToMatchLabel(best.Type)}(exact{tempContextSuffix})", criteria, options);
        }

        // Priority 2: Loose/raw dark exact exposure.
        var p2 = darkCandidates
            .Where(d => IsRawDarkType(d.Type) && Math.Abs(d.ExposureTime - exposure) < ExactExposureTolerance)
            .ToList();
        if (p2.Count > 0)
        {
            var best = SelectBestByScore(p2, criteria, options);
            _logger.LogInformation("P2 selected exact loose dark: {Path} (type={Type}, exp={Exp:F3}s)", best.FilePath, best.Type, best.ExposureTime);
            return BuildMatch(best, optimizeRequired: false, $"P2 {ToMatchLabel(best.Type)}(exact{tempContextSuffix})", criteria, options);
        }

        // Priority 3: Master dark within +/-2s.
        var p3 = darkCandidates
            .Where(d => IsMasterDarkType(d.Type) && IsWithinDelta(d.ExposureTime, exposure, ExactExposureTolerance, NearTwoSeconds))
            .ToList();
        if (p3.Count > 0)
        {
            var best = SelectNearest(p3, exposure, criteria, options);
            return BuildMatch(
                best,
                optimizeRequired: false,
                string.Format(CultureInfo.InvariantCulture, "P3 {0}(nearest<=2s,{1:F3}s{2})", ToMatchLabel(best.Type), best.ExposureTime, tempContextSuffix),
                criteria,
                options);
        }

        // Priority 4: Loose/raw dark within +/-2s.
        var p4 = darkCandidates
            .Where(d => IsRawDarkType(d.Type) && IsWithinDelta(d.ExposureTime, exposure, ExactExposureTolerance, NearTwoSeconds))
            .ToList();
        if (p4.Count > 0)
        {
            var best = SelectNearest(p4, exposure, criteria, options);
            return BuildMatch(
                best,
                optimizeRequired: false,
                string.Format(CultureInfo.InvariantCulture, "P4 {0}(nearest<=2s,{1:F3}s{2})", ToMatchLabel(best.Type), best.ExposureTime, tempContextSuffix),
                criteria,
                options);
        }

        // Priority 5: Master dark within +/-10s (outside +/-2s).
        var p5 = darkCandidates
            .Where(d => IsMasterDarkType(d.Type) && IsWithinDelta(d.ExposureTime, exposure, NearTwoSeconds, NearTenSeconds))
            .ToList();
        if (p5.Count > 0)
        {
            var best = SelectNearest(p5, exposure, criteria, options);
            return BuildMatch(
                best,
                optimizeRequired: false,
                string.Format(CultureInfo.InvariantCulture, "P5 {0}(nearest<=10s,{1:F3}s{2})", ToMatchLabel(best.Type), best.ExposureTime, tempContextSuffix),
                criteria,
                options);
        }

        // Priority 6: Loose/raw dark within +/-10s (outside +/-2s).
        var p6 = darkCandidates
            .Where(d => IsRawDarkType(d.Type) && IsWithinDelta(d.ExposureTime, exposure, NearTwoSeconds, NearTenSeconds))
            .ToList();
        if (p6.Count > 0)
        {
            var best = SelectNearest(p6, exposure, criteria, options);
            return BuildMatch(
                best,
                optimizeRequired: false,
                string.Format(CultureInfo.InvariantCulture, "P6 {0}(nearest<=10s,{1:F3}s{2})", ToMatchLabel(best.Type), best.ExposureTime, tempContextSuffix),
                criteria,
                options);
        }

        return null;
    }

    public Task<List<string>> ValidateDarkCatalogAsync(
        IEnumerable<DarkFrame> darkCatalog,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var darks = darkCatalog.ToList();

        if (darks.Count == 0)
        {
            warnings.Add("Dark catalog is empty");
            return Task.FromResult(warnings);
        }

        // Check for missing files
        var missingFiles = darks.Where(d => !File.Exists(d.FilePath)).ToList();
        if (missingFiles.Count > 0)
        {
            warnings.Add($"{missingFiles.Count} dark files not found on disk");
        }

        // Check exposure coverage
        var exposures = darks.Select(d => d.ExposureTime).Distinct().OrderBy(e => e).ToList();
        warnings.Add($"Dark catalog covers {exposures.Count} unique exposures: {string.Join(", ", exposures.Select(e => e.ToString("F3", CultureInfo.InvariantCulture) + "s"))}");

        // Check for groups with insufficient frames
        var grouped = darks.GroupBy(d => (d.Type, d.ExposureTime));
        var insufficient = grouped.Where(g => g.Key.Type is ImageType.Dark or ImageType.DarkFlat && g.Count() < 3).ToList();
        if (insufficient.Count > 0)
        {
            warnings.Add($"{insufficient.Count} dark groups have <3 frames (need masters or >=3 frames)");
        }

        return Task.FromResult(warnings);
    }

    private static DarkFrame SelectBestByScore(
        List<DarkFrame> candidates,
        MatchingCriteria criteria,
        DarkMatchingOptions options)
    {
        return candidates
            .OrderByDescending(d => GetEffectiveScore(d, criteria, options))
            .ThenBy(d => GetTypePriority(d.Type))
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static DarkFrame SelectNearest(
        List<DarkFrame> candidates,
        double exposure,
        MatchingCriteria criteria,
        DarkMatchingOptions options)
    {
        return candidates
            .OrderBy(d => Math.Abs(d.ExposureTime - exposure))
            .ThenByDescending(d => GetEffectiveScore(d, criteria, options))
            .ThenBy(d => GetTypePriority(d.Type))
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static double GetEffectiveScore(DarkFrame d, MatchingCriteria criteria, DarkMatchingOptions options)
    {
        return d.CalculateMatchScore(criteria, options);
    }

    private static DarkMatchResult BuildMatch(
        DarkFrame selected,
        bool optimizeRequired,
        string matchKind,
        MatchingCriteria criteria,
        DarkMatchingOptions options)
    {
        return new DarkMatchResult
        {
            FilePath = selected.FilePath,
            OptimizeRequired = optimizeRequired,
            MatchKind = matchKind,
            MatchScore = selected.CalculateMatchScore(criteria, options)
        };
    }

    private static bool IsDarkCalibrationType(ImageType type)
        => type is ImageType.MasterDarkFlat
            or ImageType.DarkFlat
            or ImageType.MasterDark
            or ImageType.Dark;

    private static bool IsMasterDarkType(ImageType type)
        => type is ImageType.MasterDarkFlat or ImageType.MasterDark;

    private static bool IsRawDarkType(ImageType type)
        => type is ImageType.DarkFlat or ImageType.Dark;

    private static bool IsWithinDelta(double candidateExposure, double targetExposure, double minExclusive, double maxInclusive)
    {
        var delta = Math.Abs(candidateExposure - targetExposure);
        return delta > minExclusive && delta <= maxInclusive;
    }

    private static string ToMatchLabel(ImageType type)
    {
        return type switch
        {
            ImageType.MasterDarkFlat => "MasterDarkFlat",
            ImageType.DarkFlat => "DarkFlat",
            ImageType.MasterDark => "MasterDark",
            ImageType.Dark => "Dark",
            ImageType.MasterBias => "MasterBias",
            ImageType.Bias => "Bias",
            _ => type.ToString()
        };
    }

    private static int GetTypePriority(ImageType type)
    {
        return type switch
        {
            ImageType.MasterDarkFlat => 0,
            ImageType.MasterDark => 1,
            ImageType.DarkFlat => 2,
            ImageType.Dark => 3,
            ImageType.MasterBias => 4,
            ImageType.Bias => 5,
            _ => 10
        };
    }

    private static bool IsTemperatureAllowed(DarkFrame frame, MatchingCriteria criteria, bool preferClosestTemp, double maxTempDeltaC)
    {
        if (!preferClosestTemp)
            return true;
        if (!criteria.Temperature.HasValue || !frame.Temperature.HasValue)
            return true;

        return Math.Abs(frame.Temperature.Value - criteria.Temperature.Value) <= maxTempDeltaC;
    }

    public MatchingDiagnostic? FindBestDarkWithDiagnostics(
        string flatPath,
        ExposureGroup exposureGroup,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options)
    {
        var exposure = exposureGroup.ExposureTime;
        var criteria = exposureGroup.MatchingCriteria ?? new MatchingCriteria();
        var darks = darkCatalog.ToList();

        // Find the best match
        var bestMatch = FindBestDark(exposureGroup, darks, options);
        if (bestMatch == null)
        {
            return new MatchingDiagnostic
            {
                FlatPath = flatPath,
                ExposureTime = exposure,
                SelectedDark = null!,
                SelectionReason = "No suitable dark or bias found in catalog",
                ConfidenceScore = 0.0
            };
        }

        // Find the actual dark object
        var selectedDark = darks.FirstOrDefault(d => d.FilePath == bestMatch.FilePath);
        if (selectedDark == null)
            return null;

        // Calculate confidence and temperature delta
        var tempDelta = selectedDark.Temperature.HasValue && criteria.Temperature.HasValue
            ? Math.Abs(selectedDark.Temperature.Value - criteria.Temperature.Value)
            : null as double?;

        var warnings = new List<string>();
        var confidence = bestMatch.MatchScore;

        if (bestMatch.OptimizeRequired)
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "Exposure optimization required: {0:F3}s -> {1:F3}s", selectedDark.ExposureTime, exposure));

        if (tempDelta.HasValue && tempDelta > 5.0)
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "Large temperature delta: {0:F1} C", tempDelta));

        // Find rejected alternatives
        var rejected = new List<DarkMatchingCandidate>();
        var otherCandidates = darks
            .Where(d => d.FilePath != bestMatch.FilePath)
            .OrderByDescending(d => d.CalculateMatchScore(criteria, options))
            .Take(5)
            .ToList();

        foreach (var alt in otherCandidates)
        {
            rejected.Add(new DarkMatchingCandidate
            {
                Dark = alt,
                RejectionReason = string.Format(CultureInfo.InvariantCulture, "Lower match score ({0:F2} vs {1:F2})", alt.CalculateMatchScore(criteria, options), bestMatch.MatchScore)
            });
        }

        return new MatchingDiagnostic
        {
            FlatPath = flatPath,
            ExposureTime = exposure,
            SelectedDark = selectedDark,
            SelectionReason = bestMatch.MatchKind,
            TemperatureDeltaC = tempDelta,
            RejectedAlternatives = rejected,
            ConfidenceScore = Math.Min(1.0, confidence),
            Warnings = warnings
        };
    }

    public async Task<List<MatchingDiagnostic>> GenerateMatchingDiagnosticsAsync(
        IEnumerable<DirectoryJob> jobs,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options,
        IProgress<MatchingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<MatchingDiagnostic>();
            var darkList = darkCatalog.ToList();
            var jobList = jobs.ToList();
            var totalFlats = jobList.Sum(j => j.ExposureGroups.Sum(g => g.FilePaths.Count));
            var processedFlats = 0;

            progress?.Report(new MatchingProgress
            {
                ProcessedFlats = 0,
                TotalFlats = totalFlats
            });

            foreach (var job in jobList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var group in job.ExposureGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    foreach (var flatPath in group.FilePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var diagnostic = FindBestDarkWithDiagnostics(flatPath, group, darkList, options);
                        if (diagnostic != null)
                            results.Add(diagnostic);

                        processedFlats++;
                        if (processedFlats == totalFlats || processedFlats % 25 == 0)
                        {
                            progress?.Report(new MatchingProgress
                            {
                                ProcessedFlats = processedFlats,
                                TotalFlats = totalFlats
                            });
                        }
                    }
                }
            }

            if (processedFlats != totalFlats)
            {
                progress?.Report(new MatchingProgress
                {
                    ProcessedFlats = processedFlats,
                    TotalFlats = totalFlats
                });
            }

            return results;
        }, cancellationToken);
    }
}

