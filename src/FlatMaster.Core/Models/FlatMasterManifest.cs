namespace FlatMaster.Core.Models;

public enum FlatOutputStatus { Pending, Completed, ChangedInputs, Failed }

/// <summary>Input snapshot and outcome for a single flat-group processing attempt.</summary>
public sealed class FlatMasterManifest
{
    public int Version { get; set; } = 1;
    public required string GroupKey { get; init; }
    public required string CalibrationIdentity { get; init; }
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string Engine { get; init; }
    public string State { get; set; } = "Running";
    public string? Error { get; set; }
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
    public double ExposureSeconds { get; init; }
    public string? Filter { get; init; }
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Channels { get; init; }
    public required List<SourceFrameInfo> SourceFrames { get; init; }
    public int InputFrameCount => SourceFrames.Count;
    public int? IntegratedFrameCount { get; set; }
    public string? MasterPath { get; set; }
    public long MasterLength { get; set; }
    public DateTime MasterLastWriteUtc { get; set; }
}
