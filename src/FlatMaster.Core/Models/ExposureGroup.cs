namespace FlatMaster.Core.Models;

/// <summary>
/// Represents a group of frames with the same exposure time
/// </summary>
public sealed class ExposureGroup
{
    public required double ExposureTime { get; init; }
    public required List<string> FilePaths { get; init; } = new();
    public ImageMetadata? RepresentativeMetadata { get; init; }
    
    /// <summary>
    /// Desired characteristics for matching dark frames
    /// </summary>
    public MatchingCriteria? MatchingCriteria { get; init; }
    
    /// <summary>
    /// Whether this group has enough frames for integration (minimum 3)
    /// </summary>
    public bool IsValid => FilePaths.Count >= 3;
    
    public int Count => FilePaths.Count;
}

/// <summary>
/// Criteria for matching dark frames to flats
/// </summary>
public sealed record MatchingCriteria
{
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public double? Temperature { get; init; }
}
