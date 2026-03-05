using FlatMaster.Infrastructure.Services;
using Xunit;
using FluentAssertions;

namespace FlatMaster.Tests.Services;

public class MasterDarkPathingTests
{
    [Fact]
    public void BuildMasterDarkOutputDirectory_FormatsExposureAndTemperatureFolders()
    {
        var path = MasterDarkPathing.BuildMasterDarkOutputDirectory("D:\\fmOutput", 25.0, -10.0);

        path.Replace('/', '\\').Should().EndWith("Master\\Darks\\25s\\-10degC");
    }

    [Fact]
    public void BuildMasterDarkFileName_UsesExposureAndTemperatureTokens()
    {
        var fileName = MasterDarkPathing.BuildMasterDarkFileName(25.0, -10.0);

        fileName.Should().Be("MasterDark_25s_-10degC.xisf");
    }

    [Fact]
    public void BuildMasterDarkFileName_UsesFitsWhenRequested()
    {
        var fileName = MasterDarkPathing.BuildMasterDarkFileName(25.0, -10.0, "fits");

        fileName.Should().Be("MasterDark_25s_-10degC.fits");
    }

    [Fact]
    public void BuildMasterDarkOutputDirectory_UsesUnknownForMissingTemperature()
    {
        var path = MasterDarkPathing.BuildMasterDarkOutputDirectory("D:\\fmOutput", 25.5, null);

        path.Replace('/', '\\').Should().EndWith("Master\\Darks\\25.5s\\Unknown");
    }
}
