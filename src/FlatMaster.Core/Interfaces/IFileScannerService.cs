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

