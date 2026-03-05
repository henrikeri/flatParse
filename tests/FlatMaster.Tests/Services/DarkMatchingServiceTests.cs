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
using System.IO;
using System.Threading.Tasks;
using System;
using FluentAssertions;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public class DarkMatchingServiceTests
{
    private readonly DarkMatchingService _service;

    public DarkMatchingServiceTests()
    {
        _service = new DarkMatchingService(new Mock<ILogger<DarkMatchingService>>().Object);
    }

    [Fact]
    public void FindBestDark_Priority1_MasterExactBeatsRawExact()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0),
            [
                new() { FilePath = "master_10s.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0 },
                new() { FilePath = "raw_10s_1.fit", Type = ImageType.Dark, ExposureTime = 10.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_10s.xisf");
        result.MatchKind.Should().StartWith("P1");
        result.OptimizeRequired.Should().BeFalse();
    }

    [Fact]
    public void FindBestDark_Priority2_RawExactBeatsMasterWithin2()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0),
            [
                new() { FilePath = "master_9s.xisf", Type = ImageType.MasterDark, ExposureTime = 9.0 },
                new() { FilePath = "raw_10s.fit", Type = ImageType.Dark, ExposureTime = 10.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("raw_10s.fit");
        result.MatchKind.Should().StartWith("P2");
    }

    [Fact]
    public void FindBestDark_Priority3_MasterWithin2BeatsRawWithin2()
    {
        var result = _service.FindBestDark(
            MakeGroup(20.0),
            [
                new() { FilePath = "master_18s.xisf", Type = ImageType.MasterDark, ExposureTime = 18.0 },
                new() { FilePath = "raw_19s.fit", Type = ImageType.Dark, ExposureTime = 19.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_18s.xisf");
        result.MatchKind.Should().StartWith("P3");
    }

    [Fact]
    public void FindBestDark_Priority4_RawWithin2BeatsMasterWithin10()
    {
        var result = _service.FindBestDark(
            MakeGroup(20.0),
            [
                new() { FilePath = "master_12s.xisf", Type = ImageType.MasterDark, ExposureTime = 12.0 },
                new() { FilePath = "raw_19s.fit", Type = ImageType.Dark, ExposureTime = 19.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("raw_19s.fit");
        result.MatchKind.Should().StartWith("P4");
        result.OptimizeRequired.Should().BeFalse();
    }

    [Fact]
    public void FindBestDark_Priority5_MasterWithin10BeatsRawWithin10()
    {
        var result = _service.FindBestDark(
            MakeGroup(20.0),
            [
                new() { FilePath = "master_12s.xisf", Type = ImageType.MasterDark, ExposureTime = 12.0 },
                new() { FilePath = "raw_11s.fit", Type = ImageType.Dark, ExposureTime = 11.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_12s.xisf");
        result.MatchKind.Should().StartWith("P5");
        result.OptimizeRequired.Should().BeFalse();
    }

    [Fact]
    public void FindBestDark_Priority6_RawWithin10WhenNoMasterWithin10()
    {
        var result = _service.FindBestDark(
            MakeGroup(20.0),
            [
                new() { FilePath = "master_31s.xisf", Type = ImageType.MasterDark, ExposureTime = 31.0 },
                new() { FilePath = "raw_12s.fit", Type = ImageType.Dark, ExposureTime = 12.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("raw_12s.fit");
        result.MatchKind.Should().StartWith("P6");
    }

    [Fact]
    public void FindBestDark_Priority7_BiasFallback()
    {
        var result = _service.FindBestDark(
            MakeGroup(20.0),
            [
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterbias.xisf");
        result.MatchKind.Should().StartWith("P7");
    }

    [Fact]
    public void FindBestDark_TemperatureOutsideTolerance_IsRejectedFromDarkPriorities()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -10.0),
            [
                new() { FilePath = "master_10s_warm.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0, Temperature = -7.5 },
                new() { FilePath = "raw_10s_cold.fit", Type = ImageType.Dark, ExposureTime = 10.0, Temperature = -10.2 }
            ],
            new DarkMatchingOptions { MaxTempDeltaC = 1.0 });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("raw_10s_cold.fit");
        result.MatchKind.Should().StartWith("P2");
    }

    [Fact]
    public void FindBestDark_ExactExposureWithinDarkOverBiasThreshold_BeatsBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -12.3),
            [
                new() { FilePath = "master_10s_-10C.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0, Temperature = -10.0 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -12.3 }
            ],
            new DarkMatchingOptions { MaxTempDeltaC = 1.0, DarkOverBiasTempDeltaC = 5.0, PreferClosestTemp = true });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_10s_-10C.xisf");
        result.MatchKind.Should().StartWith("P1");
    }

    [Fact]
    public void FindBestDark_ExactExposureOutsideDarkOverBiasThreshold_FallsBackToBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -12.3),
            [
                new() { FilePath = "master_10s_-10C.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0, Temperature = -10.0 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -12.3 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 2.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterbias.xisf");
        result.MatchKind.Should().StartWith("P7");
    }

    [Fact]
    public void FindBestDark_LargeTempDeltaAboveDefaultThreshold_FallsBackToBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -10.0),
            [
                new() { FilePath = "master_10s_0C.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0, Temperature = 0.1 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -10.0 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 5.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterbias.xisf");
        result.MatchKind.Should().StartWith("P7");
    }

    [Fact]
    public void FindBestDark_ExactTempDeltaAtThreshold_BeatsBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -10.0),
            [
                new() { FilePath = "master_10s_-5C.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0, Temperature = -5.0 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -10.0 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 5.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_10s_-5C.xisf");
        result.MatchKind.Should().StartWith("P1");
    }

    [Fact]
    public void FindBestDark_ExposureDeltaWithinThreshold_BeatsBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -10.0),
            [
                new() { FilePath = "master_14s_-7p5C.xisf", Type = ImageType.MasterDark, ExposureTime = 14.0, Temperature = -7.5 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -10.0 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 5.0,
                DarkOverBiasExposureDeltaSeconds = 5.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("master_14s_-7p5C.xisf");
        result.MatchKind.Should().StartWith("P5");
    }

    [Fact]
    public void FindBestDark_ExposureDeltaAboveThreshold_FallsBackToBias()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -10.0),
            [
                new() { FilePath = "master_16s_-7p5C.xisf", Type = ImageType.MasterDark, ExposureTime = 16.0, Temperature = -7.5 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0, Temperature = -10.0 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 5.0,
                DarkOverBiasExposureDeltaSeconds = 5.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("masterbias.xisf");
        result.MatchKind.Should().StartWith("P7");
    }

    [Fact]
    public void FindBestDark_ManualOverride_UsesSpecifiedDark()
    {
        var result = _service.FindBestDark(
            MakeGroup(10.0, -12.3, manualDarkPath: "forced_dark.fit"),
            [
                new() { FilePath = "forced_dark.fit", Type = ImageType.Dark, ExposureTime = 30.0, Temperature = 20.0 },
                new() { FilePath = "masterbias.xisf", Type = ImageType.MasterBias, ExposureTime = 0.0 }
            ],
            new DarkMatchingOptions
            {
                MaxTempDeltaC = 1.0,
                DarkOverBiasTempDeltaC = 5.0,
                PreferClosestTemp = true
            });

        result.Should().NotBeNull();
        result!.FilePath.Should().Be("forced_dark.fit");
        result.MatchKind.Should().StartWith("Override");
    }

    [Fact]
    public void FindBestDark_NoMatch_ReturnsNull()
    {
        var result = _service.FindBestDark(MakeGroup(1.5), [], new DarkMatchingOptions());
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateDarkCatalog_EmptyCatalog_ReturnsWarning()
    {
        var warnings = await _service.ValidateDarkCatalogAsync([]);
        warnings.Should().Contain(w => w.Contains("empty"));
    }

    [Fact]
    public async Task ValidateDarkCatalog_ValidCatalog_ReturnsExposureSummary()
    {
        var darkCatalog = new List<DarkFrame>
        {
            new() { FilePath = "dark1.fits", Type = ImageType.MasterDark, ExposureTime = 1.0 },
            new() { FilePath = "dark2.fits", Type = ImageType.MasterDark, ExposureTime = 2.0 },
            new() { FilePath = "dark3.fits", Type = ImageType.MasterDark, ExposureTime = 1.0 }
        };

        var warnings = await _service.ValidateDarkCatalogAsync(darkCatalog);
        warnings.Should().Contain(w => w.Contains("2 unique exposures"));
    }

    [Fact]
    public void FindBestDark_DoesNotWriteCandidateDumpFile()
    {
        var dumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dark_matching_candidates.log");
        if (File.Exists(dumpPath))
            File.Delete(dumpPath);

        var result = _service.FindBestDark(
            MakeGroup(10.0),
            [
                new() { FilePath = "master_10s.xisf", Type = ImageType.MasterDark, ExposureTime = 10.0 },
                new() { FilePath = "raw_10s.fit", Type = ImageType.Dark, ExposureTime = 10.0 }
            ],
            new DarkMatchingOptions());

        result.Should().NotBeNull();
        File.Exists(dumpPath).Should().BeFalse();
    }

    private static ExposureGroup MakeGroup(double exposureTime, double? temperature = null, string? manualDarkPath = null)
    {
        return new ExposureGroup
        {
            ExposureTime = exposureTime,
            FilePaths = ["f1.fit", "f2.fit", "f3.fit"],
            MatchingCriteria = new MatchingCriteria
            {
                Temperature = temperature,
                ManualDarkPath = manualDarkPath
            }
        };
    }
}
