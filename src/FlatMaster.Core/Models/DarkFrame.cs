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

using System.IO;

namespace FlatMaster.Core.Models;

/// <summary>
/// Represents a dark frame in the catalog
/// </summary>
public sealed class DarkFrame
{
    public required string FilePath { get; init; }
    public required ImageType Type { get; init; }
    public required double ExposureTime { get; init; }
    public string? Binning { get; init; }
    public double? Gain { get; init; }
    public double? Offset { get; init; }
    public double? Temperature { get; set; }
    public bool IsSelected { get; set; } = true;

    public string FileName => Path.GetFileName(FilePath);
    
    /// <summary>
    /// Calculate a matching score against desired criteria
    /// Higher scores indicate better matches
    /// </summary>
    public double CalculateMatchScore(MatchingCriteria criteria, DarkMatchingOptions options)
    {
        double score = 0.0;
        
        // Binning match (critical)
        if (options.EnforceBinning && !string.IsNullOrEmpty(criteria.Binning) && 
            !string.IsNullOrEmpty(Binning) && Binning == criteria.Binning)
        {
            score += 3.0;
        }
        
        // Gain match (important)
        if (options.PreferSameGainOffset && criteria.Gain.HasValue && Gain.HasValue)
        {
            if (Math.Abs(Gain.Value - criteria.Gain.Value) < 0.01)
                score += 2.0;
        }
        
        // Offset match (important)
        if (options.PreferSameGainOffset && criteria.Offset.HasValue && Offset.HasValue)
        {
            if (Math.Abs(Offset.Value - criteria.Offset.Value) < 0.5)
                score += 2.0;
        }
        
        // Temperature proximity (nice to have)
        if (options.PreferClosestTemp && criteria.Temperature.HasValue && Temperature.HasValue)
        {
            double tempDelta = Math.Abs(Temperature.Value - criteria.Temperature.Value);
            if (tempDelta <= options.MaxTempDeltaC)
            {
                score += 1.5 - (tempDelta * 0.2);
            }
        }
        
        return score;
    }
}

/// <summary>
/// Options for dark frame matching
/// </summary>
public sealed record DarkMatchingOptions
{
    public bool EnforceBinning { get; init; } = true;
    public bool PreferSameGainOffset { get; init; } = true;
    public bool PreferClosestTemp { get; init; } = true;
    public double MaxTempDeltaC { get; init; } = 5.0;
    public bool AllowNearestExposureWithOptimize { get; init; } = true;
}

