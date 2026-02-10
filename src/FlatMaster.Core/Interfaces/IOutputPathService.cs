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

