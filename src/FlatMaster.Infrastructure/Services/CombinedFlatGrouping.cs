using System.Text.Json;
using FlatMaster.Core.Models;

namespace FlatMaster.Infrastructure.Services;

public static class CombinedFlatGrouping
{
    public static DirectoryJob Combine(DirectoryJob job)
    {
        var original = job.ExposureGroups.SelectMany(g => g.CalibrationUnits).ToList();
        var groups = original.GroupBy(CompatibilityKey).Select(bucket =>
        {
            var units = bucket.OrderBy(g => g.ExposureTime).ToList();
            if (units.Count == 1 || units.Sum(g => g.Count) < 3) return units;
            var first = units[0];
            return new List<ExposureGroup> { new()
            {
                ExposureTime = first.ExposureTime,
                FilePaths = units.SelectMany(g => g.FilePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RepresentativeMetadata = first.RepresentativeMetadata,
                MatchingCriteria = first.MatchingCriteria,
                AverageTemperatureC = units.Where(g => g.AverageTemperatureC.HasValue).SelectMany(g =>
                    Enumerable.Repeat((double?)g.AverageTemperatureC!.Value, g.Count)).DefaultIfEmpty().Average(),
                CalibrationGroups = units
            } };
        }).SelectMany(g => g).ToList();
        var integratedPaths = groups.Where(g => g.IsValid).SelectMany(g => g.FilePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DirectoryJob
        {
            DirectoryPath = job.DirectoryPath, BaseRootPath = job.BaseRootPath,
            OutputRootPath = job.OutputRootPath, RelativeDirectory = job.RelativeDirectory,
            IsSelected = job.IsSelected, ExposureGroups = groups,
            PassthroughFiles = job.PassthroughFiles.Where(p => !integratedPaths.Contains(p)).ToList()
        };
    }

    private static string CompatibilityKey(ExposureGroup group)
    {
        var criteria = group.MatchingCriteria;
        var meta = group.RepresentativeMetadata;
        return JsonSerializer.Serialize(new
        {
            Filter = meta?.Filter?.Trim().ToUpperInvariant(),
            Binning = (criteria?.Binning ?? meta?.Binning ?? "1").Trim().ToUpperInvariant(),
            Gain = criteria?.Gain ?? meta?.Gain, Offset = criteria?.Offset ?? meta?.Offset,
            Width = criteria?.Width ?? meta?.Width ?? 0, Height = criteria?.Height ?? meta?.Height ?? 0,
            Channels = criteria?.Channels ?? meta?.Channels ?? 1
        });
    }
}
