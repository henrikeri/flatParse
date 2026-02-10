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

