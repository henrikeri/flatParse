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
using System.Linq;
using FluentAssertions;
using FlatMaster.Core.Models;
using Xunit;

namespace FlatMaster.Tests.Core;

public class ImageMetadataTests
{
    [Fact]
    public void ExposureKey_FormatsCorrectly()
    {
        // Arrange
        var metadata = new ImageMetadata
        {
            FilePath = "test.fits",
            Type = ImageType.Flat,
            ExposureTime = 1.5
        };

        // Act
        var key = metadata.ExposureKey;

        // Assert
        key.Should().Be("1.5s");
    }

    [Theory]
    [InlineData(1.0, "1s")]
    [InlineData(1.001, "1.001s")]
    [InlineData(0.5, "0.5s")]
    [InlineData(10.125, "10.125s")]
    public void ExposureKey_HandlesVariousExposures(double exposure, string expected)
    {
        // Arrange
        var metadata = new ImageMetadata
        {
            FilePath = "test.fits",
            Type = ImageType.Flat,
            ExposureTime = exposure
        };

        // Act
        var key = metadata.ExposureKey;

        // Assert
        key.Should().Be(expected);
    }

    [Fact]
    public void ExposureKey_HandlesNull()
    {
        // Arrange
        var metadata = new ImageMetadata
        {
            FilePath = "test.fits",
            Type = ImageType.Flat,
            ExposureTime = null
        };

        // Act
        var key = metadata.ExposureKey;

        // Assert
        key.Should().Be("Unknown");
    }
}

public class ExposureGroupTests
{
    [Fact]
    public void IsValid_ReturnsTrueFor3OrMoreFiles()
    {
        // Arrange
        var group = new ExposureGroup
        {
            ExposureTime = 1.0,
            FilePaths = new List<string> { "a.fits", "b.fits", "c.fits" }
        };

        // Act & Assert
        group.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IsValid_ReturnsFalseForLessThan3Files(int count)
    {
        // Arrange
        var files = Enumerable.Range(0, count).Select(i => $"file{i}.fits").ToList();
        var group = new ExposureGroup
        {
            ExposureTime = 1.0,
            FilePaths = files
        };

        // Act & Assert
        group.IsValid.Should().BeFalse();
    }
}

public class DarkFrameTests
{
    [Fact]
    public void CalculateMatchScore_PerfectMatch_ReturnsHighScore()
    {
        // Arrange
        var dark = new DarkFrame
        {
            FilePath = "dark.fits",
            Type = ImageType.MasterDark,
            ExposureTime = 1.0,
            Binning = "1X1",
            Gain = 100.0,
            Offset = 10.0,
            Temperature = -10.0
        };

        var criteria = new MatchingCriteria
        {
            Binning = "1X1",
            Gain = 100.0,
            Offset = 10.0,
            Temperature = -10.0
        };

        var options = new DarkMatchingOptions
        {
            EnforceBinning = true,
            PreferSameGainOffset = true,
            PreferClosestTemp = true,
            MaxTempDeltaC = 5.0
        };

        // Act
        var score = dark.CalculateMatchScore(criteria, options);

        // Assert
        score.Should().BeGreaterThan(7.0); // 3 (binning) + 2 (gain) + 2 (offset) + 1.5 (temp)
    }

    [Fact]
    public void CalculateMatchScore_BinningMismatch_LowersScore()
    {
        // Arrange
        var dark = new DarkFrame
        {
            FilePath = "dark.fits",
            Type = ImageType.MasterDark,
            ExposureTime = 1.0,
            Binning = "2X2",
            Gain = 100.0
        };

        var criteria = new MatchingCriteria
        {
            Binning = "1X1",
            Gain = 100.0
        };

        var options = new DarkMatchingOptions { EnforceBinning = true, PreferSameGainOffset = true };

        // Act
        var score = dark.CalculateMatchScore(criteria, options);

        // Assert
        score.Should().BeLessThan(3.0); // No binning match bonus
    }
}

