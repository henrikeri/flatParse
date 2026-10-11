using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlatMaster.Core.Models;

namespace FlatMaster.Infrastructure.Services;

public static class FlatOutputProvenance
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string CalibrationIdentity(ExposureGroup group)
    {
        var criteria = group.MatchingCriteria;
        var meta = group.RepresentativeMetadata;
        return Hash(JsonSerializer.Serialize(new
        {
            Exposure = Math.Round(group.ExposureTime, 3),
            Filter = meta?.Filter?.Trim().ToUpperInvariant(),
            Binning = (criteria?.Binning ?? meta?.Binning ?? "1").Trim().ToUpperInvariant(),
            Gain = criteria?.Gain ?? meta?.Gain,
            Offset = criteria?.Offset ?? meta?.Offset,
            Width = criteria?.Width ?? meta?.Width ?? 0,
            Height = criteria?.Height ?? meta?.Height ?? 0,
            Channels = criteria?.Channels ?? meta?.Channels ?? 1
        }));
    }

    public static string GroupKey(DirectoryJob job, ExposureGroup group)
        => Hash(Path.GetFullPath(job.DirectoryPath).ToUpperInvariant() + "|" + CalibrationIdentity(group));

    public static string OutputDirectory(DirectoryJob job, string outputRoot)
        => Path.GetFullPath(Path.Combine(outputRoot, job.RelativeDirectory == "." ? "" : job.RelativeDirectory));

    public static string StatePath(DirectoryJob job, ExposureGroup group, string outputRoot)
        => Path.Combine(OutputDirectory(job, outputRoot), ".FlatMaster", GroupKey(job, group) + ".json");

    public static string ReceiptPath(string sentinel, DirectoryJob job, ExposureGroup group)
        => sentinel + "." + GroupKey(job, group) + ".master";

    public static FlatMasterManifest Begin(DirectoryJob job, ExposureGroup group, string engine)
    {
        var meta = group.RepresentativeMetadata;
        var criteria = group.MatchingCriteria;
        var manifest = new FlatMasterManifest
        {
            GroupKey = GroupKey(job, group), CalibrationIdentity = CalibrationIdentity(group),
            SourceDirectory = Path.GetFullPath(job.DirectoryPath),
            OutputDirectory = OutputDirectory(job, job.OutputRootPath), Engine = engine,
            ExposureSeconds = group.ExposureTime, Filter = meta?.Filter,
            Binning = criteria?.Binning ?? meta?.Binning, Gain = criteria?.Gain ?? meta?.Gain,
            Offset = criteria?.Offset ?? meta?.Offset, Width = criteria?.Width ?? meta?.Width ?? 0,
            Height = criteria?.Height ?? meta?.Height ?? 0, Channels = criteria?.Channels ?? meta?.Channels ?? 1,
            SourceFrames = SnapshotSources(group.FilePaths), MasterPath = group.ExistingMasterPath
        };
        SaveState(manifest);
        return manifest;
    }

    public static void Complete(FlatMasterManifest manifest, string masterPath, int? integratedFrameCount = null)
    {
        var master = new FileInfo(masterPath);
        if (!master.Exists || master.Length == 0)
            throw new IOException("Output master was not written: " + masterPath);
        if (!string.Equals(master.DirectoryName, manifest.OutputDirectory, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output master is outside its mirrored group directory: " + masterPath);
        manifest.MasterPath = master.FullName;
        manifest.IntegratedFrameCount = integratedFrameCount;
        manifest.MasterLength = master.Length;
        manifest.MasterLastWriteUtc = master.LastWriteTimeUtc;
        manifest.State = "Completed";
        manifest.Error = null;
        manifest.RecordedUtc = DateTime.UtcNow;
        SaveAtomically(master.FullName + ".meta.json", manifest);
        SaveState(manifest);
    }

    public static void CompleteFromReceipt(FlatMasterManifest manifest, string receiptPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(receiptPath));
        var masterPath = json.RootElement.GetProperty("masterPath").GetString()
            ?? throw new InvalidDataException("Completion receipt has no master path");
        var count = json.RootElement.GetProperty("integratedFrameCount").GetInt32();
        if (count < 3 || count > manifest.InputFrameCount)
            throw new InvalidDataException("Completion receipt has an invalid integrated frame count");
        Complete(manifest, masterPath, count);
    }

    public static void Fail(FlatMasterManifest manifest, string error)
    {
        manifest.State = "Failed";
        manifest.Error = error;
        manifest.RecordedUtc = DateTime.UtcNow;
        SaveState(manifest);
    }

    public static FlatMasterManifest? Read(string path)
        => JsonSerializer.Deserialize<FlatMasterManifest>(File.ReadAllText(path));

    public static bool SourcesMatch(FlatMasterManifest manifest, ExposureGroup group)
    {
        var paths = group.FilePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count != manifest.SourceFrames.Count) return false;
        return manifest.SourceFrames.All(source =>
        {
            var file = new FileInfo(source.Path);
            return paths.Contains(file.FullName) && file.Exists && file.Length == source.Length &&
                file.LastWriteTimeUtc == source.LastWriteUtc;
        });
    }

    public static bool MasterMatches(FlatMasterManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.MasterPath)) return false;
        var file = new FileInfo(manifest.MasterPath);
        return file.Exists && file.Length > 0 && file.Length == manifest.MasterLength &&
            file.LastWriteTimeUtc == manifest.MasterLastWriteUtc;
    }

    private static List<SourceFrameInfo> SnapshotSources(IEnumerable<string> paths)
        => paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                var info = new FileInfo(path);
                return new SourceFrameInfo { Path = info.FullName, Length = info.Exists ? info.Length : -1, LastWriteUtc = info.LastWriteTimeUtc };
            }).ToList();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void SaveState(FlatMasterManifest manifest)
        => SaveAtomically(Path.Combine(manifest.OutputDirectory, ".FlatMaster", manifest.GroupKey + ".json"), manifest);

    private static void SaveAtomically(string path, FlatMasterManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
