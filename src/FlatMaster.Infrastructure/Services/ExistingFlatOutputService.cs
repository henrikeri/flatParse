using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using FlatMaster.Core.Models;

namespace FlatMaster.Infrastructure.Services;

/// <summary>Finds completed flat outputs in the configured mirrored output tree.</summary>
public static class ExistingFlatOutputService
{
    public static void Refresh(IEnumerable<DirectoryJob> jobs, string outputRoot, CancellationToken cancellationToken = default)
    {
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = OutputDirectory(job, outputRoot);
            // Enumerate each mirrored directory once, never unrelated output subtrees.
            var masters = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => new FileInfo(path).Length > 0)
                    .Select(path => (Path: path, Match: Regex.Match(Path.GetFileName(path),
                        @"^MasterFlat_(?:20\d{2}-\d{2}-\d{2}|UNKNOWNDATE)_(.+?)(?:_Bin([^_]+))?_((?:Mixed)?[0-9]+(?:\.[0-9]+)?(?:-[0-9]+(?:\.[0-9]+)?)*)s\.(?:xisf|fit|fits)$",
                        RegexOptions.IgnoreCase)))
                    .Where(item => item.Match.Success).ToList()
                : [];

            foreach (var group in job.ExposureGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                group.ExistingMasterPath = null;
                group.OutputStatus = FlatOutputStatus.Pending;
                group.OutputStatusDetail = "No output master found";
                var statePath = FlatOutputProvenance.StatePath(job, group, outputRoot);
                if (File.Exists(statePath))
                {
                    ApplyManifest(group, statePath, directory);
                    continue;
                }
                if (!group.IsValid)
                {
                    if (group.FilePaths.Count > 0 && group.FilePaths.All(path => IsPreserved(job, outputRoot, path)))
                    {
                        group.ExistingMasterPath = Path.Combine(directory, Path.GetFileName(group.FilePaths[0]));
                        group.OutputStatus = FlatOutputStatus.Completed;
                        group.OutputStatusDetail = "Files already preserved unchanged";
                    }
                    continue;
                }

                var filters = GuessFilters(job, group);
                foreach (var master in masters)
                {
                    var match = master.Match;
                    var exposureMatches = group.IsCombined
                        ? string.Equals(match.Groups[3].Value, group.OutputExposureTag, StringComparison.OrdinalIgnoreCase)
                        : double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposure) &&
                          Math.Abs(exposure - group.ExposureTime) <= 0.0005;
                    if (!exposureMatches || !filters.Contains(match.Groups[1].Value))
                        continue;
                    var binning = group.MatchingCriteria?.Binning ?? "1";
                    if (match.Groups[2].Success && !string.Equals(match.Groups[2].Value, binning, StringComparison.OrdinalIgnoreCase))
                        continue;
                    group.ExistingMasterPath = master.Path;
                    var sidecar = master.Path + ".meta.json";
                    if (File.Exists(sidecar))
                    {
                        ApplyManifest(group, sidecar, directory);
                        // Prefer an output with the correct calibration identity if
                        // older outputs for other camera settings share this directory.
                        if (group.OutputStatus == FlatOutputStatus.ChangedInputs &&
                            group.OutputStatusDetail.StartsWith("Calibration settings"))
                            continue;
                    }
                    else
                    {
                        group.OutputStatus = FlatOutputStatus.Completed;
                        group.OutputStatusDetail = "Existing master; legacy output has no recorded input inventory";
                    }
                    break;
                }
            }
        }
    }

    public static bool IsPreserved(DirectoryJob job, string outputRoot, string source)
    {
        var destination = Path.Combine(OutputDirectory(job, outputRoot), Path.GetFileName(source));
        return File.Exists(destination) && new FileInfo(destination).Length > 0 &&
            new FileInfo(destination).Length == new FileInfo(source).Length;
    }

    public static bool HasPendingWork(DirectoryJob job, string outputRoot)
        => job.ExposureGroups.Any(group => group.NeedsProcessing && group.FilePaths.Count != 2) ||
           job.PassthroughFiles.Any(path => !IsPreserved(job, outputRoot, path));

    private static string OutputDirectory(DirectoryJob job, string outputRoot)
        => FlatOutputProvenance.OutputDirectory(job, outputRoot);

    private static void ApplyManifest(ExposureGroup group, string path, string outputDirectory)
    {
        try
        {
            var manifest = FlatOutputProvenance.Read(path);
            if (manifest == null || manifest.Version != 1)
                throw new InvalidDataException("Unsupported output manifest");
            if (!SameDirectory(manifest.OutputDirectory, outputDirectory) ||
                (manifest.MasterPath != null && !SameDirectory(Path.GetDirectoryName(manifest.MasterPath), outputDirectory)))
            {
                // The output tree was moved or copied after the manifest was written, so its
                // recorded paths are stale. Never abort the scan over it.
                ApplyRelocatedManifest(group, manifest, outputDirectory);
                return;
            }
            if (manifest.MasterPath != null && File.Exists(manifest.MasterPath))
                group.ExistingMasterPath = manifest.MasterPath;
            if (manifest.CalibrationIdentity != FlatOutputProvenance.CalibrationIdentity(group))
            {
                group.OutputStatus = FlatOutputStatus.ChangedInputs;
                group.OutputStatusDetail = "Calibration settings differ from the existing master";
            }
            else if (manifest.State != "Completed")
            {
                group.OutputStatus = FlatOutputStatus.Failed;
                group.OutputStatusDetail = manifest.Error ?? "Previous processing attempt was interrupted";
            }
            else if (manifest.MasterPath == null || !File.Exists(manifest.MasterPath))
            {
                group.OutputStatus = FlatOutputStatus.Pending;
                group.OutputStatusDetail = "Recorded output master is missing";
                group.ExistingMasterPath = null;
            }
            else if (!FlatOutputProvenance.MasterMatches(manifest))
            {
                group.OutputStatus = FlatOutputStatus.Failed;
                group.OutputStatusDetail = "Output master changed or is incomplete since it was recorded";
            }
            else if (!FlatOutputProvenance.SourcesMatch(manifest, group))
            {
                group.OutputStatus = FlatOutputStatus.ChangedInputs;
                group.OutputStatusDetail = $"Input inventory changed; existing master used {manifest.InputFrameCount} input frames, current group has {group.Count}";
            }
            else
            {
                group.OutputStatus = FlatOutputStatus.Completed;
                group.OutputStatusDetail = manifest.IntegratedFrameCount.HasValue
                    ? $"Recorded {manifest.Engine} output; {manifest.IntegratedFrameCount} integrated from {manifest.InputFrameCount} input frames"
                    : $"Recorded {manifest.Engine} output; {manifest.InputFrameCount} input frames";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException or NotSupportedException or ArgumentException)
        {
            group.OutputStatus = FlatOutputStatus.Failed;
            group.OutputStatusDetail = "Cannot verify output manifest: " + ex.Message;
        }
    }

    private static void ApplyRelocatedManifest(ExposureGroup group, FlatMasterManifest manifest, string outputDirectory)
    {
        if (group.ExistingMasterPath == null && !string.IsNullOrWhiteSpace(manifest.MasterPath))
        {
            var rebased = Path.Combine(outputDirectory, Path.GetFileName(manifest.MasterPath));
            if (File.Exists(rebased) && new FileInfo(rebased).Length > 0)
                group.ExistingMasterPath = rebased;
        }

        if (manifest.CalibrationIdentity != FlatOutputProvenance.CalibrationIdentity(group))
        {
            group.OutputStatus = FlatOutputStatus.ChangedInputs;
            group.OutputStatusDetail = "Calibration settings differ from the existing master";
        }
        else if (manifest.State != "Completed")
        {
            group.OutputStatus = FlatOutputStatus.Failed;
            group.OutputStatusDetail = manifest.Error ?? "Previous processing attempt was interrupted";
        }
        else if (group.ExistingMasterPath == null)
        {
            group.OutputStatus = FlatOutputStatus.Pending;
            group.OutputStatusDetail = "Recorded output master is missing from the current output folder";
        }
        else
        {
            // File sizes and input paths recorded at the old location can't be compared
            // reliably, so treat it like a legacy output whose master is present.
            group.OutputStatus = FlatOutputStatus.Completed;
            group.OutputStatusDetail = "Existing master; manifest was recorded for a different output location (folder moved?), inputs not re-verified";
        }
    }

    private static bool SameDirectory(string? left, string? right)
        => string.Equals(left?.TrimEnd('\\', '/'), right?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GuessFilters(DirectoryJob job, ExposureGroup group)
    {
        var filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(group.RepresentativeMetadata?.Filter))
            filters.Add(group.RepresentativeMetadata.Filter);
        var directoryName = Path.GetFileName(job.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar));
        filters.Add(directoryName);
        if (Regex.IsMatch(directoryName, @"^\d{4}-\d{2}-\d{2}$"))
            filters.Add("UNKNOWN");
        foreach (var path in group.FilePaths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var native = Regex.Match(name, @"(?:^|[_\-])(?:FILTER)?[_\-]?([LRGBSHO]a?|Ha|SII|OIII|NII)(?:[_\-]|$)", RegexOptions.IgnoreCase);
            if (native.Success) filters.Add(native.Groups[1].Value);
            var pixInsight = Regex.Match(name, @"(?:^|[_\-])(?:FILTER|Filter)[_\-]?([A-Za-z0-9]+)");
            if (pixInsight.Success) filters.Add(pixInsight.Groups[1].Value);
        }
        return filters;
    }
}
