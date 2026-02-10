using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

/// <summary>
/// Service for scanning directories and cataloging image files
/// </summary>
public interface IFileScannerService
{
    /// <summary>
    /// Scan directories recursively for flat frames, grouping by exposure
    /// </summary>
    Task<List<DirectoryJob>> ScanFlatDirectoriesAsync(
        IEnumerable<string> baseRoots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Scan directories recursively for dark frames
    /// </summary>
    Task<List<DarkFrame>> ScanDarkLibraryAsync(
        IEnumerable<string> darkRoots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Find all image files in a directory (non-recursive)
    /// </summary>
    Task<List<string>> GetImageFilesAsync(
        string directory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Progress report for scanning operations
/// </summary>
public sealed record ScanProgress
{
    public int DirectoriesScanned { get; init; }
    public int FilesFound { get; init; }
    public int FitsFound { get; init; }
    public int XisfFound { get; init; }
    public string? CurrentDirectory { get; init; }
}
