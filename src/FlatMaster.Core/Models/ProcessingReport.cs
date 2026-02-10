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
/// Summary report of a processing operation
/// </summary>
public sealed record ProcessingReport
{
    /// <summary>
    /// Timestamp when processing started
    /// </summary>
    public required DateTime StartTime { get; init; }

    /// <summary>
    /// Timestamp when processing completed
    /// </summary>
    public DateTime? EndTime { get; init; }

    /// <summary>
    /// Total duration of processing
    /// </summary>
    public TimeSpan? TotalDuration => EndTime.HasValue ? EndTime.Value - StartTime : null;

    /// <summary>
    /// Total flat files processed
    /// </summary>
    public int TotalFlatsProcessed { get; init; }

    /// <summary>
    /// Total flat files that succeeded
    /// </summary>
    public int FlatsSucceeded { get; init; }

    /// <summary>
    /// Total flat files that failed
    /// </summary>
    public int FlatsFailed { get; init; }

    /// <summary>
    /// Unique exposure groups found
    /// </summary>
    public int UniqueExposureGroups { get; init; }

    /// <summary>
    /// Unique dark frame combinations used
    /// </summary>
    public int UniqueDarkMastersCreated { get; init; }

    /// <summary>
    /// How many times dark masters were reused from cache
    /// </summary>
    public int DarkMasterCacheHits { get; init; }

    /// <summary>
    /// Total bytes of calibrated flat frames created
    /// </summary>
    public long CalibratedFlatsBytes { get; init; }

    /// <summary>
    /// Total bytes of dark master frames created
    /// </summary>
    public long DarkMastersBytes { get; init; }

    /// <summary>
    /// Total bytes of final master calibration images
    /// </summary>
    public long MasterCalibrationBytes { get; init; }

    /// <summary>
    /// Output directory where results were saved
    /// </summary>
    public required string OutputRootDirectory { get; init; }

    /// <summary>
    /// Whether output was replicated to separate tree or mixed in source
    /// </summary>
    public bool ReplicatedOutputTree { get; init; }

    /// <summary>
    /// Average temperature delta of selected darks (in Celsius)
    /// </summary>
    public double AverageTemperatureDelta { get; init; }

    /// <summary>
    /// Min/max temperature deltas observed
    /// </summary>
    public double MinTemperatureDelta { get; init; }
    public double MaxTemperatureDelta { get; init; }

    /// <summary>
    /// PixInsight script path used
    /// </summary>
    public string? ScriptPath { get; init; }

    /// <summary>
    /// Processing log/console output from PixInsight
    /// </summary>
    public string ProcessingLog { get; init; } = string.Empty;

    /// <summary>
    /// Any errors that occurred
    /// </summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Warnings about the processing
    /// </summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// Per-flat matching diagnostics
    /// </summary>
    public List<MatchingDiagnostic> MatchingDiagnostics { get; init; } = new();
}

