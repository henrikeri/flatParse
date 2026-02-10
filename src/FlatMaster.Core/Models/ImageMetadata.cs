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

using System.Globalization;

namespace FlatMaster.Core.Models;

/// <summary>
/// Represents metadata extracted from FITS/XISF image headers
/// </summary>
public sealed record ImageMetadata
{
    public required string FilePath { get; init; }
    public required ImageType Type { get; init; }
    public double? ExposureTime { get; init; }
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public double? Temperature { get; init; }
    public string? Filter { get; init; }
    public DateTime? ObservationDate { get; init; }
    
    /// <summary>
    /// Format exposure time for consistent display (3 decimal places, strip trailing zeros)
    /// </summary>
    public string ExposureKey => ExposureTime.HasValue 
        ? Math.Round(ExposureTime.Value, 3).ToString("0.###", CultureInfo.InvariantCulture) + "s"
        : "Unknown";
}

/// <summary>
/// Types of astronomical images
/// </summary>
public enum ImageType
{
    Unknown,
    Light,
    Flat,
    Dark,
    DarkFlat,
    Bias,
    MasterFlat,
    MasterDark,
    MasterDarkFlat,
    MasterBias
}

