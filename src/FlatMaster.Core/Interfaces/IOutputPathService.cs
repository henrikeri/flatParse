using FlatMaster.Core.Models;

namespace FlatMaster.Core.Interfaces;

/// <summary>
/// Service for managing output paths and directory structures
/// </summary>
public interface IOutputPathService
{
    /// <summary>
    /// Determine the output path for a processed file
    /// </summary>
    string GetOutputPath(
        string sourceFilePath,
        string sourceRoot,
        OutputPathConfiguration config,
        string fileType); // "dark_master", "calibrated_flat", "master_calibration"

    /// <summary>
    /// Ensure all necessary output directories exist
    /// </summary>
    Task InitializeOutputDirectoriesAsync(OutputPathConfiguration config);

    /// <summary>
    /// Replicate directory structure from source tree to output tree
    /// </summary>
    Task ReplicateDirectoryStructureAsync(
        string sourceRoot,
        OutputPathConfiguration config);
}
