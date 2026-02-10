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
/// Complete plan for processing all selected directories
/// </summary>
public sealed class ProcessingPlan
{
    public required List<DirectoryJob> Jobs { get; init; } = new();
    public required List<DarkFrame> DarkCatalog { get; init; } = new();
    public required ProcessingConfiguration Configuration { get; init; }
    
    /// <summary>
    /// Get only selected jobs
    /// </summary>
    public IEnumerable<DirectoryJob> SelectedJobs => Jobs.Where(j => j.IsSelected);
    
    /// <summary>
    /// Get only selected dark frames
    /// </summary>
    public IEnumerable<DarkFrame> SelectedDarks => DarkCatalog.Where(d => d.IsSelected);
}

/// <summary>
/// Configuration for processing operation
/// </summary>
public sealed record ProcessingConfiguration
{
    public required string PixInsightExecutable { get; init; }
    public bool DeleteCalibratedFlats { get; init; } = true;
    public string CacheDirName { get; init; } = "_DarkMasters";
    public string CalibratedSubdirBase { get; init; } = "_CalibratedFlats";
    public string MasterSubdirName { get; init; } = "Masters";
    public string XisfHintsCal { get; init; } = "";
    public string XisfHintsMaster { get; init; } = "compression-codec zlib+sh; compression-level 9; checksum sha1";
    public RejectionSettings Rejection { get; init; } = new();
    public DarkMatchingOptions DarkMatching { get; init; } = new();
}

/// <summary>
/// Settings for pixel rejection during integration
/// </summary>
public sealed record RejectionSettings
{
    public double LowSigma { get; init; } = 5.0;
    public double HighSigma { get; init; } = 5.0;
}

