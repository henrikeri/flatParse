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
/// Represents a group of frames with the same exposure time
/// </summary>
public sealed class ExposureGroup
{
    public required double ExposureTime { get; init; }
    public required List<string> FilePaths { get; init; } = new();
    public ImageMetadata? RepresentativeMetadata { get; init; }
    
    /// <summary>
    /// Desired characteristics for matching dark frames
    /// </summary>
    public MatchingCriteria? MatchingCriteria { get; init; }
    
    /// <summary>
    /// Whether this group has enough frames for integration (minimum 3)
    /// </summary>
    public bool IsValid => FilePaths.Count >= 3;
    
    public int Count => FilePaths.Count;
}

/// <summary>
/// Criteria for matching dark frames to flats
/// </summary>
public sealed record MatchingCriteria
{
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public double? Temperature { get; init; }
}

