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
