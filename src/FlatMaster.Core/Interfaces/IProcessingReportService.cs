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
/// Service for generating processing reports
/// </summary>
public interface IProcessingReportService
{
    /// <summary>
    /// Generate a summary report from processing results
    /// </summary>
    ProcessingReport GenerateReport(
        DateTime startTime,
        IEnumerable<MatchingDiagnostic> matchingDiagnostics,
        IEnumerable<DarkFrame> darkCatalog,
        ProcessingConfiguration config,
        OutputPathConfiguration outputConfig);

    /// <summary>
    /// Format a report as human-readable text
    /// </summary>
    string FormatReportAsText(ProcessingReport report);

    /// <summary>
    /// Export report as JSON
    /// </summary>
    Task ExportReportAsJsonAsync(ProcessingReport report, string filePath);
}

