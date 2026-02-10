using System.Globalization;
using System.Text;
using System.Text.Json;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Generates and formats processing reports
/// </summary>
public sealed class ProcessingReportService : IProcessingReportService
{
    private readonly ILogger<ProcessingReportService> _logger;

    public ProcessingReportService(ILogger<ProcessingReportService> logger)
    {
        _logger = logger;
    }

    public ProcessingReport GenerateReport(
        DateTime startTime,
        IEnumerable<MatchingDiagnostic> matchingDiagnostics,
        IEnumerable<DarkFrame> darkCatalog,
        ProcessingConfiguration config,
        OutputPathConfiguration outputConfig)
    {
        var diagnosticList = matchingDiagnostics.ToList();
        var darkList = darkCatalog.ToList();

        var succeeded = diagnosticList.Count(d => d.SelectedDark != null);
        var failed = diagnosticList.Count(d => d.SelectedDark == null);

        var tempDeltas = diagnosticList
            .Where(d => d.TemperatureDeltaC.HasValue)
            .Select(d => d.TemperatureDeltaC!.Value)
            .ToList();

        var exposureGroups = diagnosticList
            .Select(d => d.ExposureTime)
            .Distinct()
            .Count();

        var uniqueDarks = diagnosticList
            .Where(d => d.SelectedDark != null)
            .Select(d => d.SelectedDark.FilePath)
            .Distinct()
            .Count();

        return new ProcessingReport
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            TotalFlatsProcessed = diagnosticList.Count,
            FlatsSucceeded = succeeded,
            FlatsFailed = failed,
            UniqueExposureGroups = exposureGroups,
            UniqueDarkMastersCreated = uniqueDarks,
            DarkMasterCacheHits = 0, // Will be populated by pixel insight service
            AverageTemperatureDelta = tempDeltas.Any() ? tempDeltas.Average() : 0,
            MinTemperatureDelta = tempDeltas.Any() ? tempDeltas.Min() : 0,
            MaxTemperatureDelta = tempDeltas.Any() ? tempDeltas.Max() : 0,
            OutputRootDirectory = outputConfig.OutputRootPath,
            ReplicatedOutputTree = outputConfig.Mode == OutputMode.ReplicatedSeparateTree,
            MatchingDiagnostics = diagnosticList
        };
    }

    public string FormatReportAsText(ProcessingReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("╔════════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║           FLATMASTER PROCESSING REPORT                         ║");
        sb.AppendLine("╚════════════════════════════════════════════════════════════════╝");
        sb.AppendLine();

        // Summary Section
        sb.AppendLine("📊 PROCESSING SUMMARY");
        sb.AppendLine("─────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Start Time:           {report.StartTime:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  End Time:             {report.EndTime:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  Duration:             {report.TotalDuration?.ToString(@"hh\:mm\:ss") ?? "N/A"}");
        sb.AppendLine();

        // Files Processed
        sb.AppendLine("📁 FILES PROCESSED");
        sb.AppendLine("─────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Total Flats:          {report.TotalFlatsProcessed}");
        sb.AppendLine($"  ✓ Succeeded:          {report.FlatsSucceeded}");
        sb.AppendLine($"  ✗ Failed:             {report.FlatsFailed}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Success Rate:         {0:F1}%", report.TotalFlatsProcessed > 0 ? (report.FlatsSucceeded * 100.0 / report.TotalFlatsProcessed) : 0));
        sb.AppendLine();

        // Dark Frame Matching
        sb.AppendLine("🌑 DARK FRAME MATCHING");
        sb.AppendLine("─────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Unique Exposures:     {report.UniqueExposureGroups}");
        sb.AppendLine($"  Dark Masters Used:    {report.UniqueDarkMastersCreated}");
        sb.AppendLine($"  Cache Hits:           {report.DarkMasterCacheHits}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Avg Temp Delta:       {0:F2}°C", report.AverageTemperatureDelta));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Temp Range:           {0:F2}°C to {1:F2}°C", report.MinTemperatureDelta, report.MaxTemperatureDelta));
        sb.AppendLine();

        // Output Configuration
        sb.AppendLine("💾 OUTPUT CONFIGURATION");
        sb.AppendLine("─────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Mode:                 {(report.ReplicatedOutputTree ? "Replicated Separate Tree" : "Inline in Source")}");
        sb.AppendLine($"  Root Directory:       {report.OutputRootDirectory}");
        sb.AppendLine();

        // Storage Statistics
        sb.AppendLine("📦 STORAGE USAGE");
        sb.AppendLine("─────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Calibrated Flats:     {FormatBytes(report.CalibratedFlatsBytes)}");
        sb.AppendLine($"  Dark Masters:         {FormatBytes(report.DarkMastersBytes)}");
        sb.AppendLine($"  Master Calibration:   {FormatBytes(report.MasterCalibrationBytes)}");
        sb.AppendLine($"  Total Generated:      {FormatBytes(report.CalibratedFlatsBytes + report.DarkMastersBytes + report.MasterCalibrationBytes)}");
        sb.AppendLine();

        // Warnings/Errors
        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("⚠️  WARNINGS");
            sb.AppendLine("─────────────────────────────────────────────────────────────────");
            foreach (var warning in report.Warnings)
            {
                sb.AppendLine($"  • {warning}");
            }
            sb.AppendLine();
        }

        if (report.Errors.Count > 0)
        {
            sb.AppendLine("❌ ERRORS");
            sb.AppendLine("─────────────────────────────────────────────────────────────────");
            foreach (var error in report.Errors)
            {
                sb.AppendLine($"  • {error}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("═══════════════════════════════════════════════════════════════════");

        return sb.ToString();
    }

    public async Task ExportReportAsJsonAsync(ProcessingReport report, string filePath)
    {
        try
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogInformation("Report exported to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export report to {FilePath}", filePath);
            throw;
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        if (bytes >= gb)
            return (bytes / (double)gb).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= mb)
            return (bytes / (double)mb).ToString("F2", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= kb)
            return (bytes / (double)kb).ToString("F2", CultureInfo.InvariantCulture) + " KB";
        return $"{bytes} B";
    }
}
