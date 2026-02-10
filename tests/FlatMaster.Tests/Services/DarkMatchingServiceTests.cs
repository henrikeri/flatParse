using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public class DarkMatchingServiceTests
{
    private readonly Mock<ILogger<DarkMatchingService>> _loggerMock;
    private readonly DarkMatchingService _service;

    public DarkMatchingServiceTests()
    {
        _loggerMock = new Mock<ILogger<DarkMatchingService>>();
        _service = new DarkMatchingService(_loggerMock.Object);
    }

    [Fact]
    public void FindBestDark_ExactMasterDarkFlat_ReturnsMatch()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 1.5,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria { Binning = "1X1" }
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdarkflat_1.5s.xisf",
                Type = ImageType.MasterDarkFlat,
                ExposureTime = 1.5,
                Binning = "1X1"
            }
        };

        var options = new DarkMatchingOptions();

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterdarkflat_1.5s.xisf");
        result.MatchKind.Should().Be("MasterDarkFlat(exact)");
        result.OptimizeRequired.Should().BeFalse();
    }

    [Fact]
    public void FindBestDark_NoExactMatch_ReturnsNearestWithOptimize()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 1.5,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria()
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdark_1.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 1.0
            },
            new DarkFrame
            {
                FilePath = "masterdark_2.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 2.0
            }
        };

        var options = new DarkMatchingOptions { AllowNearestExposureWithOptimize = true };

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.OptimizeRequired.Should().BeTrue();
        result.MatchKind.Should().Contain("nearest+optimize");
    }

    [Fact]
    public void FindBestDark_NoMatch_ReturnsNull()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 1.5,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" }
        };

        var darkCatalog = new List<DarkFrame>();
        var options = new DarkMatchingOptions();

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateDarkCatalog_EmptyCatalog_ReturnsWarning()
    {
        // Arrange
        var darkCatalog = new List<DarkFrame>();

        // Act
        var warnings = await _service.ValidateDarkCatalogAsync(darkCatalog);

        // Assert
        warnings.Should().Contain(w => w.Contains("empty"));
    }

    [Fact]
    public async Task ValidateDarkCatalog_ValidCatalog_ReturnsExposureSummary()
    {
        // Arrange
        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame { FilePath = "dark1.fits", Type = ImageType.MasterDark, ExposureTime = 1.0 },
            new DarkFrame { FilePath = "dark2.fits", Type = ImageType.MasterDark, ExposureTime = 2.0 },
            new DarkFrame { FilePath = "dark3.fits", Type = ImageType.MasterDark, ExposureTime = 1.0 }
        };

        // Act
        var warnings = await _service.ValidateDarkCatalogAsync(darkCatalog);

        // Assert
        warnings.Should().Contain(w => w.Contains("2 unique exposures"));
    }
}
