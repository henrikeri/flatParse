using System.Collections.Generic;
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
    }

    [Fact]
    public void GeneratePJSRScript_DarkMaterializePlan_UsesDarkTemplateWithoutCalibrationFlow()
    {
        var plan = BuildPlan(relativeDirectory: "__DARKMATERIALIZE__/Master/Darks/25s/-10degC", exposure: 25.0);

        var script = _service.GeneratePJSRScript(plan);

        script.Should().NotContain("ImageCalibration");
        script.Should().Contain("DARK integrate:");
    }

    [Fact]
    public void GeneratePJSRScript_IncludesConfiguredOutputExtension()
    {
        var plan = BuildPlan(relativeDirectory: "Flats/Blue", exposure: 25.0, outputFileExtension: "fits");

        var script = _service.GeneratePJSRScript(plan);

        script.Should().Contain("outputExtension:\"fits\"");
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
