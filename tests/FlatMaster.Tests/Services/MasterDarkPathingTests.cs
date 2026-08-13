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

    [Fact]
    public void BuildMasterDarkOutputDirectory_SeparatesCalibrationIdentities()
    {
        var first = MasterDarkPathing.BuildMasterDarkOutputDirectory("D:\\fmOutput", 25, -10, "1", 100, 20);
        var second = MasterDarkPathing.BuildMasterDarkOutputDirectory("D:\\fmOutput", 25, -10, "1", 100, 30);

        first.Should().NotBe(second);
        first.Replace('/', '\\').Should().EndWith("25s\\-10degC\\Bin1_Gain100_Offset20");
    }

    [Fact]
    public void BuildMasterDarkOutputDirectory_SeparatesImageGeometries()
    {
        var full = MasterDarkPathing.BuildMasterDarkOutputDirectory(
            "D:\\fmOutput", 20, -10, "1", 100, 20, 9576, 6388, 1);
        var roi = MasterDarkPathing.BuildMasterDarkOutputDirectory(
            "D:\\fmOutput", 20, -10, "1", 100, 20, 1936, 1096, 1);

        full.Should().NotBe(roi);
        full.Replace('/', '\\').Should().EndWith("Bin1_Gain100_Offset20_Res9576x6388x1");
        roi.Replace('/', '\\').Should().EndWith("Bin1_Gain100_Offset20_Res1936x1096x1");
    }
}
