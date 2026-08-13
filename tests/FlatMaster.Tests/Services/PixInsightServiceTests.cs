using System.Collections.Generic;
using System.Threading.Tasks;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public class PixInsightServiceTests
{
    private readonly PixInsightService _service = new(new Mock<ILogger<PixInsightService>>().Object);

    [Fact]
    public void GeneratePJSRScript_FlatPlan_UsesFlatTemplateWithCalibrationFlow()
    {
        var plan = BuildPlan(relativeDirectory: "Flats/Blue", exposure: 25.0);

        var script = _service.GeneratePJSRScript(plan);

        script.Should().Contain("ImageCalibration");
        script.Should().Contain("calibrateFlats");
        script.Should().Contain("requireDarks:true");
        script.Should().Contain("if (CFG.requireDarks)");
        script.Should().Contain("optimize: true");
        script.Should().Contain("minimumCalibratedFlatMedian:0.01");
        script.Should().Contain("flatSignalSampleGrid:16");
        script.Should().Contain("rejectLowSignalCalibratedFlats");
        script.Should().Contain("sampledImageMedian");
        script.Should().Contain("publishOutput(masterTemp, masterOut)");
        script.Should().Contain("var biasMasterCache = {};");
        script.Should().Contain("P7 Bias(cached)");
        script.Should().Contain("Cannot replace cached master bias");
        script.Should().Contain("\"Master Bias\"");
        script.Should().Contain("IC.masterBiasEnabled = true;");
        script.Should().Contain("IC.masterBiasPath = calibrationPath;");
        script.Should().Contain("calibration=\"+(sel.isBias ? \"bias\" : \"dark\")");
        script.Should().NotContain("SKIP (master already exists)");
    }

    [Fact]
    public void GeneratePJSRScript_DarkMaterializePlan_UsesDarkTemplateWithoutCalibrationFlow()
    {
        var plan = BuildPlan(relativeDirectory: "__DARKMATERIALIZE__/Master/Darks/25s/-10degC", exposure: 25.0);

        var script = _service.GeneratePJSRScript(plan);

        script.Should().NotContain("ImageCalibration");
        script.Should().Contain("DARK integrate:");
        script.Should().Contain("publishOutput(masterTemp, masterOut)");
        script.Should().NotContain("SKIP (master already exists)");
    }

    [Fact]
    public void GeneratePJSRScript_IncludesConfiguredOutputExtension()
    {
        var plan = BuildPlan(relativeDirectory: "Flats/Blue", exposure: 25.0, outputFileExtension: "fits");

        var script = _service.GeneratePJSRScript(plan);

        script.Should().Contain("outputExtension:\"fits\"");
    }

    [Fact]
    public async Task ProcessJobsInBatchesAsync_PreservationOnlyPlan_DoesNotRequirePixInsight()
    {
        var plan = new ProcessingPlan
        {
            Jobs =
            [
                new DirectoryJob
                {
                    DirectoryPath = "D:/input",
                    BaseRootPath = "D:/input",
                    OutputRootPath = "D:/output",
                    RelativeDirectory = "single",
                    ExposureGroups = [],
                    PassthroughFiles = ["D:/input/only_flat.fit"],
                    IsSelected = true
                }
            ],
            DarkCatalog = [],
            Configuration = new ProcessingConfiguration
            {
                PixInsightExecutable = "Z:/missing/PixInsight.exe",
                DarkMatching = new DarkMatchingOptions(),
                Rejection = new RejectionSettings()
            }
        };

        var result = await _service.ProcessJobsInBatchesAsync(
            plan,
            plan.Configuration.PixInsightExecutable,
            batchSize: 25);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("No jobs to process.");
    }

    private static ProcessingPlan BuildPlan(string relativeDirectory, double exposure, string outputFileExtension = "xisf")
    {
        return new ProcessingPlan
        {
            Jobs =
            [
                new DirectoryJob
                {
                    DirectoryPath = "D:/input",
                    BaseRootPath = "D:/input",
                    OutputRootPath = "D:/output",
                    RelativeDirectory = relativeDirectory,
                    ExposureGroups =
                    [
                        new ExposureGroup
                        {
                            ExposureTime = exposure,
                            FilePaths = ["D:/input/a1.fit", "D:/input/a2.fit", "D:/input/a3.fit"],
                            MatchingCriteria = new MatchingCriteria { Temperature = -10.0 }
                        }
                    ],
                    IsSelected = true
                }
            ],
            DarkCatalog =
            [
                new DarkFrame
                {
                    FilePath = "D:/darks/master_dark_25s.xisf",
                    Type = ImageType.MasterDark,
                    ExposureTime = 25.0,
                    Temperature = -10.0,
                    IsSelected = true
                }
            ],
            Configuration = new ProcessingConfiguration
            {
                PixInsightExecutable = "C:/Program Files/PixInsight/bin/PixInsight.exe",
                OutputFileExtension = outputFileExtension,
                DarkMatching = new DarkMatchingOptions(),
                Rejection = new RejectionSettings()
            }
        };
    }
}
