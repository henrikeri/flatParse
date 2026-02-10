namespace FlatMaster.Core.Models;

/// <summary>
/// Represents a processing job for a single directory containing flats
/// </summary>
public sealed class DirectoryJob
{
    public required string DirectoryPath { get; init; }
    public required string BaseRootPath { get; init; }
    public required string OutputRootPath { get; init; }
    public required string RelativeDirectory { get; init; }
    public required List<ExposureGroup> ExposureGroups { get; init; } = new();
    public bool IsSelected { get; set; } = true;
    
    /// <summary>
    /// Total number of flat files in this directory across all exposure groups
    /// </summary>
    public int TotalFileCount => ExposureGroups.Sum(g => g.Count);
    
    /// <summary>
    /// Number of valid exposure groups (>=3 files each)
    /// </summary>
    public int ValidGroupCount => ExposureGroups.Count(g => g.IsValid);
}
