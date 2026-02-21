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
    public void FindBestDark_NoExactMatchWithin2s_ReturnsNearestWithoutOptimize()
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
        result!.OptimizeRequired.Should().BeFalse();
        result.MatchKind.Should().Contain("nearest<=2s");
    }

    [Fact]
    public void FindBestDark_NoExactMatchWithin10s_ReturnsNearestWithOptimize()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 15.0,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria()
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdark_8.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 8.0
            },
            new DarkFrame
            {
                FilePath = "masterdark_30.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 30.0
            }
        };

        var options = new DarkMatchingOptions { AllowNearestExposureWithOptimize = true };

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.OptimizeRequired.Should().BeTrue();
        result.MatchKind.Should().Contain("nearest<=10s+optimize");
    }

    [Fact]
    public void FindBestDark_Exactly2sDelta_IncludesInNoOptimizeTier()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 20.0,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria()
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdark_18.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 18.0
            }
        };

        var options = new DarkMatchingOptions { AllowNearestExposureWithOptimize = true };

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.OptimizeRequired.Should().BeFalse();
        result.MatchKind.Should().Contain("nearest<=2s");
    }

    [Fact]
    public void FindBestDark_Exactly10sDelta_IncludesInOptimizeTier()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 20.0,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria()
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdark_10.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 10.0
            }
        };

        var options = new DarkMatchingOptions { AllowNearestExposureWithOptimize = true };

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.OptimizeRequired.Should().BeTrue();
        result.MatchKind.Should().Contain("nearest<=10s+optimize");
    }

    [Fact]
    public void FindBestDark_NearestDisabled_FallsBackToBias()
    {
        // Arrange
        var exposureGroup = new ExposureGroup
        {
            ExposureTime = 15.0,
            FilePaths = new List<string> { "flat1.fits", "flat2.fits", "flat3.fits" },
            MatchingCriteria = new MatchingCriteria()
        };

        var darkCatalog = new List<DarkFrame>
        {
            new DarkFrame
            {
                FilePath = "masterdark_8.0s.xisf",
                Type = ImageType.MasterDark,
                ExposureTime = 8.0
            },
            new DarkFrame
            {
                FilePath = "masterbias.xisf",
                Type = ImageType.MasterBias,
                ExposureTime = 0.0
            }
        };

        var options = new DarkMatchingOptions { AllowNearestExposureWithOptimize = false };

        // Act
        var result = _service.FindBestDark(exposureGroup, darkCatalog, options);

        // Assert
        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterbias.xisf");
        result.MatchKind.Should().Be("MasterBias");
        result.OptimizeRequired.Should().BeFalse();
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

