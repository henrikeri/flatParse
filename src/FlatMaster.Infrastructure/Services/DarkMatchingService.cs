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

        // 1. Try MasterDarkFlat with exact exposure
        var result = TryFindBest(darks, ImageType.MasterDarkFlat, exposure, criteria, options, "MasterDarkFlat(exact)", optimize: false);
        if (result != null) return result;

        // 2. Try DarkFlat with exact exposure (will need integration)
        var darkFlats = darks.Where(d => d.Type == ImageType.DarkFlat && Math.Abs(d.ExposureTime - exposure) < 0.001).ToList();
        if (darkFlats.Count >= 3)
        {
            var best = darkFlats.OrderByDescending(d => d.CalculateMatchScore(criteria, options)).First();
            return new DarkMatchResult
            {
                FilePath = best.FilePath,
                OptimizeRequired = false,
                MatchKind = "DarkFlat(integrate)",
                MatchScore = best.CalculateMatchScore(criteria, options)
            };
        }

        // 3. Try MasterDark with exact exposure
        result = TryFindBest(darks, ImageType.MasterDark, exposure, criteria, options, "MasterDark(exact)", optimize: false);
        if (result != null) return result;

        // 4. Try Dark with exact exposure (will need integration)
        var darkRaws = darks.Where(d => d.Type == ImageType.Dark && Math.Abs(d.ExposureTime - exposure) < 0.001).ToList();
        if (darkRaws.Count >= 3)
        {
            var best = darkRaws.OrderByDescending(d => d.CalculateMatchScore(criteria, options)).First();
            return new DarkMatchResult
            {
                FilePath = best.FilePath,
                OptimizeRequired = false,
                MatchKind = "Dark(integrate)",
                MatchScore = best.CalculateMatchScore(criteria, options)
            };
        }

        // 5. Try nearest exposure with optimize (if enabled)
        if (options.AllowNearestExposureWithOptimize)
        {
            var masters = darks.Where(d => d.Type is ImageType.MasterDark or ImageType.MasterDarkFlat).ToList();
            if (masters.Count > 0)
            {
                var nearest = masters
                    .OrderBy(d => Math.Abs(d.ExposureTime - exposure))
                    .ThenByDescending(d => d.CalculateMatchScore(criteria, options))
                    .First();

                return new DarkMatchResult
                {
                    FilePath = nearest.FilePath,
                    OptimizeRequired = true,
                    MatchKind = string.Format(CultureInfo.InvariantCulture, "MasterDark(nearest+optimize, {0:F3}s)", nearest.ExposureTime),
                    MatchScore = nearest.CalculateMatchScore(criteria, options)
                };
            }
        }

        _logger.LogWarning("No suitable dark found for exposure {Exposure}s", exposure);
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

    private DarkMatchResult? TryFindBest(
        List<DarkFrame> darks,
        ImageType type,
        double exposure,
        MatchingCriteria criteria,
        DarkMatchingOptions options,
        string matchKind,
        bool optimize)
    {
        var candidates = darks
            .Where(d => d.Type == type && Math.Abs(d.ExposureTime - exposure) < 0.001)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var best = candidates
            .OrderByDescending(d => d.CalculateMatchScore(criteria, options))
            .First();

        return new DarkMatchResult
        {
            FilePath = best.FilePath,
            OptimizeRequired = optimize,
            MatchKind = matchKind,
            MatchScore = best.CalculateMatchScore(criteria, options)
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
                SelectionReason = "No suitable dark frame found in catalog",
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
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "Exposure optimization required: {0:F3}s → {1:F3}s", selectedDark.ExposureTime, exposure));

        if (tempDelta.HasValue && tempDelta > 5.0)
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "Large temperature delta: {0:F1}°C", tempDelta));

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