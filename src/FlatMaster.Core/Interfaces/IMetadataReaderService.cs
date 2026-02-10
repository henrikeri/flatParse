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

