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
    public required List<string> FilePaths { get; init; } = [];
    public ImageMetadata? RepresentativeMetadata { get; init; }
    public double? AverageTemperatureC { get; init; }
    /// <summary>Original exposure groups calibrated separately before joint integration.</summary>
    public List<ExposureGroup> CalibrationGroups { get; init; } = [];
    public bool IsCombined => CalibrationGroups.Count > 0;
    public IEnumerable<ExposureGroup> CalibrationUnits => IsCombined ? CalibrationGroups : [this];
    public string ExposureDisplay => IsCombined
        ? string.Join(" + ", CalibrationGroups.Select(g => g.ExposureTime).Distinct().OrderBy(x => x)
            .Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + " s (combined)"
        : ExposureTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
    public string OutputExposureTag => IsCombined
        ? "Mixed" + string.Join("-", CalibrationGroups.Select(g => g.ExposureTime).Distinct().OrderBy(x => x)
            .Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))
        : ExposureTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Existing output for this group, refreshed before matching and processing.</summary>
    public string? ExistingMasterPath { get; set; }
    public FlatOutputStatus OutputStatus { get; set; }
    public string OutputStatusDetail { get; set; } = "No output master found";
    public bool NeedsProcessing => OutputStatus != FlatOutputStatus.Completed;

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
    public int Width { get; init; }
    public int Height { get; init; }
    public int Channels { get; init; } = 1;
    public string? ManualDarkPath { get; init; }
}

