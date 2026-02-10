using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

/// <summary>
/// Service for reading metadata from astronomical image files
/// </summary>
public interface IMetadataReaderService
{
    /// <summary>
    /// Read metadata from a single file
    /// </summary>
    Task<ImageMetadata?> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Read metadata from multiple files in parallel
    /// </summary>
    Task<Dictionary<string, ImageMetadata>> ReadMetadataBatchAsync(
        IEnumerable<string> filePaths, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Check if file format is supported (FITS, XISF)
    /// </summary>
    bool IsSupportedFormat(string filePath);
}
