using System;
using System.IO;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public class ProcessingReportServiceTests
{
    [Fact]
    public void FormatReportAsText_WithDuration_DoesNotThrowAndFormatsDuration()
    {
        var service = new ProcessingReportService(new Mock<ILogger<ProcessingReportService>>().Object);
        var report = new ProcessingReport
        {
            StartTime = new DateTime(2026, 2, 27, 15, 50, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 2, 27, 16, 15, 38, DateTimeKind.Utc),
            OutputRootDirectory = @"D:\fmOutput"
        };

        var text = service.FormatReportAsText(report);

        text.Should().Contain("Duration:");
        text.Should().Contain("00:25:38");
    }

    [Fact]
    public void FormatReportAsText_ShowsDeltaAndMatchedDarkTemperatureRanges()
    {
        var service = new ProcessingReportService(new Mock<ILogger<ProcessingReportService>>().Object);
        var report = new ProcessingReport
        {
            StartTime = new DateTime(2026, 2, 27, 15, 50, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 2, 27, 15, 51, 0, DateTimeKind.Utc),
            OutputRootDirectory = @"D:\fmOutput",
            AverageTemperatureDelta = 0.02,
            MinTemperatureDelta = 0.0,
            MaxTemperatureDelta = 0.1,
            MinSelectedDarkTemperatureC = -10.2,
            MaxSelectedDarkTemperatureC = -9.9
        };

        var text = service.FormatReportAsText(report);

        text.Should().Contain("Temp Delta Range:");
        text.Should().Contain("0.00 degC to 0.10 degC");
        text.Should().Contain("Matched Dark Temp:");
        text.Should().Contain("-10.20 degC to -9.90 degC");
    }

    [Fact]
    public void GenerateReport_ComputesGeneratedStorageUsageUnderOutputRoot()
    {
        var service = new ProcessingReportService(new Mock<ILogger<ProcessingReportService>>().Object);
        var outputRoot = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);
        try
        {
            var runStartUtc = DateTime.UtcNow;

            var darkPath = Path.Combine(outputRoot, "Master", "Darks", "25s", "-10degC", "MasterDark_25s_-10degC.xisf");
            Directory.CreateDirectory(Path.GetDirectoryName(darkPath)!);
            File.WriteAllBytes(darkPath, new byte[1234]);

            var calPath = Path.Combine(outputRoot, "M56", "_CalibratedFlats_25s", "cal_001.xisf");
            Directory.CreateDirectory(Path.GetDirectoryName(calPath)!);
            File.WriteAllBytes(calPath, new byte[2345]);

            var masterFlatPath = Path.Combine(outputRoot, "M56", "MasterFlat_2025-09-12_LUM_25s.xisf");
            Directory.CreateDirectory(Path.GetDirectoryName(masterFlatPath)!);
            File.WriteAllBytes(masterFlatPath, new byte[3456]);

            var report = service.GenerateReport(
                runStartUtc,
                [],
                [],
                new ProcessingConfiguration
                {
                    PixInsightExecutable = "C:/Program Files/PixInsight/bin/PixInsight.exe",
                    CalibratedSubdirBase = "_CalibratedFlats",
                    MasterSubdirName = "Masters"
                },
                new OutputPathConfiguration
                {
                    Mode = OutputMode.ReplicatedSeparateTree,
                    OutputRootPath = outputRoot
                });

            report.DarkMastersBytes.Should().Be(1234);
            report.CalibratedFlatsBytes.Should().Be(2345);
            report.MasterCalibrationBytes.Should().Be(3456);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }
}
