namespace FlatMaster.Core.Models;

public sealed class MaterializationCandidate
{
    public required string Folder { get; init; }
    public double? ExposureSeconds { get; init; }
    public double? TemperatureC { get; init; }
    public int FrameCount { get; init; }
}
