// Copyright (C) 2026 Henrik E. Riise
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

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

