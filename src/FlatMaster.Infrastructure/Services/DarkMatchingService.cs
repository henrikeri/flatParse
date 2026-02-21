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
public sealed class DarkMatchingService : IDarkMatchingService
{
    private const double ExactExposureTolerance = 0.001;
    private readonly ILogger<DarkMatchingService> _logger;

    public DarkMatchingService(ILogger<DarkMatchingService> logger)
    {
        _logger = logger;
    }

    public DarkMatchResult? FindBestDark(
        ExposureGroup exposureGroup,
        IEnumerable<DarkFrame> darkCatalog,
        DarkMatchingOptions options)
    {
        var exposure = exposureGroup.ExposureTime;
        var criteria = exposureGroup.MatchingCriteria ?? new MatchingCriteria();
        var darks = darkCatalog.ToList();

        // 1. Exact exposure darks (score decides by temp/gain/offset/binning).
        var exactDarks = darks
            .Where(d => IsDarkCalibrationType(d.Type) && Math.Abs(d.ExposureTime - exposure) < ExactExposureTolerance)
            .ToList();
        if (exactDarks.Count > 0)
        {
            var bestExact = SelectBestByScore(exactDarks, criteria, options);
            return BuildMatch(bestExact, optimizeRequired: false, $"{ToMatchLabel(bestExact.Type)}(exact)", criteria, options);
        }

        if (options.AllowNearestExposureWithOptimize)
        {
            var darkCandidates = darks.Where(d => IsDarkCalibrationType(d.Type)).ToList();

            // 2. Nearest dark at or under 2 seconds delta, no optimization.
            var nearNoOptimize = darkCandidates
                .Where(d =>
                {
                    var delta = Math.Abs(d.ExposureTime - exposure);
                    return delta >= ExactExposureTolerance && delta <= 2.0;
                })
                .ToList();

            if (nearNoOptimize.Count > 0)
            {
                var bestNear = SelectNearest(nearNoOptimize, exposure, criteria, options);
                return BuildMatch(
                    bestNear,
                    optimizeRequired: false,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}(nearest<=2s,{1:F3}s)",
                        ToMatchLabel(bestNear.Type),
                        bestNear.ExposureTime),
                    criteria,
                    options);
            }

            // 3. Nearest dark over 2 and at or under 10 seconds delta, with optimization.
            var nearOptimize = darkCandidates
                .Where(d =>
                {
                    var delta = Math.Abs(d.ExposureTime - exposure);
                    return delta > 2.0 && delta <= 10.0;
                })
                .ToList();

            if (nearOptimize.Count > 0)
            {
                var bestNear = SelectNearest(nearOptimize, exposure, criteria, options);
                return BuildMatch(
                    bestNear,
                    optimizeRequired: true,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}(nearest<=10s+optimize,{1:F3}s)",
                        ToMatchLabel(bestNear.Type),
                        bestNear.ExposureTime),
                    criteria,
                    options);
            }
        }

        // 4. Bias fallback.
        var biasCandidates = darks
            .Where(d => d.Type is ImageType.MasterBias or ImageType.Bias)
            .ToList();
        if (biasCandidates.Count > 0)
        {
            var bestBias = SelectBestByScore(biasCandidates, criteria, options);
            return BuildMatch(bestBias, optimizeRequired: false, ToMatchLabel(bestBias.Type), criteria, options);
        }

        _logger.LogWarning("No suitable dark or bias found for exposure {Exposure}s", exposure);
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
            .OrderByDescending(d => d.CalculateMatchScore(criteria, options))
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
            .ThenByDescending(d => d.CalculateMatchScore(criteria, options))
            .ThenBy(d => GetTypePriority(d.Type))
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .First();
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
            ImageType.DarkFlat => 1,
            ImageType.MasterDark => 2,
            ImageType.Dark => 3,
            ImageType.MasterBias => 4,
            ImageType.Bias => 5,
            _ => 10
        };
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
        CancellationToken cancellationToken = default)
    {
        var results = new List<MatchingDiagnostic>();
        var darkList = darkCatalog.ToList();

        foreach (var job in jobs)
        {
            foreach (var group in job.ExposureGroups)
            {
                foreach (var flatPath in group.FilePaths)
                {
                    var diagnostic = FindBestDarkWithDiagnostics(flatPath, group, darkList, options);
                    if (diagnostic != null)
                    {
                        results.Add(diagnostic);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return await Task.FromResult(results);
    }
}
