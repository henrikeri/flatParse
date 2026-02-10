namespace FlatMaster.Core.Models;

/// <summary>
/// Configuration for where and how to output processed files
/// </summary>
public sealed record OutputPathConfiguration
{
    /// <summary>
    /// Output mode: inline (same tree) or replicated (separate tree)
    /// </summary>
    public required OutputMode Mode { get; init; }

    /// <summary>
    /// Root directory for output files
    /// </summary>
    public required string OutputRootPath { get; init; }

    /// <summary>
    /// When Mode=Replicated, replicate directory structure from source
    /// </summary>
    public bool ReplicateDirectoryStructure { get; init; } = true;

    /// <summary>
    /// When Mode=Replicated, copy ONLY processed files (don't include non-image files)
    /// </summary>
    public bool CopyOnlyProcessedFiles { get; init; } = true;

    /// <summary>
    /// Subdirectory for dark masters (relative to output root)
    /// </summary>
    public string DarkMastersSubdir { get; init; } = "_DarkMasters";

    /// <summary>
    /// Subdirectory for calibrated flats (relative to output root)
    /// </summary>
    public string CalibratedFlatsSubdir { get; init; } = "_CalibratedFlats";

    /// <summary>
    /// Subdirectory for final masters (relative to output root)
    /// </summary>
    public string MasterCalibrationSubdir { get; init; } = "Masters";

    /// <summary>
    /// Delete calibrated flats after creating master (save space)
    /// </summary>
    public bool DeleteCalibratedFlatsAfterMaster { get; init; } = true;
}

/// <summary>
/// Output placement mode
/// </summary>
public enum OutputMode
{
    /// <summary>
    /// Output goes into source directory tree alongside originals
    /// </summary>
    InlineInSource = 0,

    /// <summary>
    /// Output replicated to separate directory tree, preserving structure
    /// </summary>
    ReplicatedSeparateTree = 1
}
