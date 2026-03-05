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
using System.Text;
using System.Text.Json;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Generates and formats processing reports.
/// </summary>
public sealed class ProcessingReportService(ILogger<ProcessingReportService> logger) : IProcessingReportService
{
    private readonly ILogger<ProcessingReportService> _logger = logger;

    public ProcessingReport GenerateReport(
        DateTime startTime,
        IEnumerable<MatchingDiagnostic> matchingDiagnostics,
        IEnumerable<DarkFrame> darkCatalog,
        ProcessingConfiguration config,
        OutputPathConfiguration outputConfig)
    {
        var diagnosticList = matchingDiagnostics.ToList();
        var (calibratedBytes, darkMasterBytes, masterCalibrationBytes) =
            ComputeGeneratedStorageBytes(outputConfig.OutputRootPath, config, startTime);

        var succeeded = diagnosticList.Count(d =>
            d.SelectedDark != null ||
            (!config.RequireDarks && d.SelectionReason.Contains("No suitable dark", StringComparison.OrdinalIgnoreCase)));
        var failed = diagnosticList.Count - succeeded;

        var tempDeltas = diagnosticList
            .Where(d => d.TemperatureDeltaC.HasValue)
            .Select(d => d.TemperatureDeltaC!.Value)
            .ToList();

        var selectedDarkTemps = diagnosticList
            .Select(d => d.SelectedDark?.Temperature)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
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
            DarkMasterCacheHits = 0,
            CalibratedFlatsBytes = calibratedBytes,
            DarkMastersBytes = darkMasterBytes,
            MasterCalibrationBytes = masterCalibrationBytes,
            AverageTemperatureDelta = tempDeltas.Count > 0 ? tempDeltas.Average() : 0,
            MinTemperatureDelta = tempDeltas.Count > 0 ? tempDeltas.Min() : 0,
            MaxTemperatureDelta = tempDeltas.Count > 0 ? tempDeltas.Max() : 0,
            MinSelectedDarkTemperatureC = selectedDarkTemps.Count > 0 ? selectedDarkTemps.Min() : null,
            MaxSelectedDarkTemperatureC = selectedDarkTemps.Count > 0 ? selectedDarkTemps.Max() : null,
            OutputRootDirectory = outputConfig.OutputRootPath,
            ReplicatedOutputTree = outputConfig.Mode == OutputMode.ReplicatedSeparateTree,
            MatchingDiagnostics = diagnosticList
        };
    }

    public string FormatReportAsText(ProcessingReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("FLATMASTER PROCESSING REPORT");
        sb.AppendLine(new string('=', 65));
        sb.AppendLine();

        sb.AppendLine("PROCESSING SUMMARY");
        sb.AppendLine(new string('-', 65));
        sb.AppendLine($"  Start Time:           {report.StartTime:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  End Time:             {report.EndTime:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine($"  Duration:             {report.TotalDuration?.ToString(@"hh\:mm\:ss") ?? "N/A"}");
        sb.AppendLine();

        sb.AppendLine("FILES PROCESSED");
        sb.AppendLine(new string('-', 65));
        sb.AppendLine($"  Total Flats:          {report.TotalFlatsProcessed}");
        sb.AppendLine($"  Succeeded:            {report.FlatsSucceeded}");
        sb.AppendLine($"  Failed:               {report.FlatsFailed}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "  Success Rate:         {0:F1}%",
            report.TotalFlatsProcessed > 0 ? (report.FlatsSucceeded * 100.0 / report.TotalFlatsProcessed) : 0));
        sb.AppendLine();

        sb.AppendLine("DARK FRAME MATCHING");
        sb.AppendLine(new string('-', 65));
        sb.AppendLine($"  Unique Exposures:     {report.UniqueExposureGroups}");
        sb.AppendLine($"  Dark Masters Used:    {report.UniqueDarkMastersCreated}");
        sb.AppendLine($"  Cache Hits:           {report.DarkMasterCacheHits}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Avg Temp Delta:       {0:F2} degC", report.AverageTemperatureDelta));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Temp Delta Range:     {0:F2} degC to {1:F2} degC", report.MinTemperatureDelta, report.MaxTemperatureDelta));
        if (report.MinSelectedDarkTemperatureC.HasValue && report.MaxSelectedDarkTemperatureC.HasValue)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Matched Dark Temp:    {0:F2} degC to {1:F2} degC",
                report.MinSelectedDarkTemperatureC.Value,
                report.MaxSelectedDarkTemperatureC.Value));
        }
        sb.AppendLine();

        sb.AppendLine("OUTPUT CONFIGURATION");
        sb.AppendLine(new string('-', 65));
        sb.AppendLine($"  Mode:                 {(report.ReplicatedOutputTree ? "Replicated Separate Tree" : "Inline in Source")}");
        sb.AppendLine($"  Root Directory:       {report.OutputRootDirectory}");
        sb.AppendLine();

        sb.AppendLine("STORAGE USAGE");
        sb.AppendLine(new string('-', 65));
        sb.AppendLine($"  Calibrated Flats:     {FormatBytes(report.CalibratedFlatsBytes)}");
        sb.AppendLine($"  Dark Masters:         {FormatBytes(report.DarkMastersBytes)}");
        sb.AppendLine($"  Master Calibration:   {FormatBytes(report.MasterCalibrationBytes)}");
        sb.AppendLine($"  Total Generated:      {FormatBytes(report.CalibratedFlatsBytes + report.DarkMastersBytes + report.MasterCalibrationBytes)}");
        sb.AppendLine();

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("WARNINGS");
            sb.AppendLine(new string('-', 65));
            foreach (var warning in report.Warnings)
                sb.AppendLine($"  - {warning}");
            sb.AppendLine();
        }

        if (report.Errors.Count > 0)
        {
            sb.AppendLine("ERRORS");
            sb.AppendLine(new string('-', 65));
            foreach (var error in report.Errors)
                sb.AppendLine($"  - {error}");
            sb.AppendLine();
        }

        sb.AppendLine(new string('=', 65));
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

    private static (long Calibrated, long DarkMasters, long MasterCalibration) ComputeGeneratedStorageBytes(
        string outputRootPath,
        ProcessingConfiguration config,
        DateTime runStartUtc)
    {
        if (string.IsNullOrWhiteSpace(outputRootPath) || !Directory.Exists(outputRootPath))
            return (0, 0, 0);

        long calibrated = 0;
        long darkMasters = 0;
        long masterCalibration = 0;
        var cutoffUtc = runStartUtc.AddSeconds(-2);
        var normalizedCalibratedMarker = "/" + (config.CalibratedSubdirBase ?? "_CalibratedFlats");
        var normalizedMasterSubdir = "/" + (config.MasterSubdirName ?? "Masters") + "/";

        foreach (var filePath in Directory.EnumerateFiles(outputRootPath, "*.*", SearchOption.AllDirectories))
        {
            if (!IsImageFile(filePath))
                continue;

            DateTime lastWriteUtc;
            long length;
            try
            {
                var fi = new FileInfo(filePath);
                lastWriteUtc = fi.LastWriteTimeUtc;
                length = fi.Length;
            }
            catch
            {
                continue;
            }

            if (lastWriteUtc < cutoffUtc)
                continue;

            var normalizedPath = filePath.Replace('\\', '/');
            var fileName = Path.GetFileName(filePath);
            if (normalizedPath.Contains("/Master/Darks/", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("MasterDark", StringComparison.OrdinalIgnoreCase))
            {
                darkMasters += length;
                continue;
            }

            if (normalizedPath.Contains(normalizedCalibratedMarker, StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("cal_", StringComparison.OrdinalIgnoreCase))
            {
                calibrated += length;
                continue;
            }

            if (normalizedPath.Contains(normalizedMasterSubdir, StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("MasterFlat", StringComparison.OrdinalIgnoreCase))
            {
                masterCalibration += length;
            }
        }

        return (calibrated, darkMasters, masterCalibration);
    }

    private static bool IsImageFile(string filePath)
    {
        return filePath.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".fit", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase);
    }
}
