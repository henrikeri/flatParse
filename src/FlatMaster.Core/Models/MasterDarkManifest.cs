using System.Text.Json.Serialization;

namespace FlatMaster.Core.Models;

public sealed class MasterDarkManifest
{
    public int PipelineVersion { get; init; } = 1;
    public required string Key { get; init; }
    public required double ExposureSeconds { get; init; }
    public string? CameraId { get; init; }
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double? TemperatureMedianC { get; init; }
    public required List<SourceFrameInfo> SourceFrames { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class SourceFrameInfo
{
    public required string Path { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public string? Hash { get; init; }
}
