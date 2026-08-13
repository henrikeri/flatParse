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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class FileScannerServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IMetadataReaderService> _metadataReader = new();
    private readonly FileScannerService _scanner;

    public FileScannerServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FlatMaster_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _scanner = new FileScannerService(_metadataReader.Object, Mock.Of<ILogger<FileScannerService>>());
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_ParsesFlatsAndSkipsDarkTypes()
    {
        var files = new[]
        {
            CreateFile("flat_001.fits"),
            CreateFile("flat_002.fits"),
            CreateFile("flat_003.fits"),
            CreateFile("dark_001.fits"),
            CreateFile("dark_002.fits"),
            CreateFile("dark_003.fits")
        };

        var metadata = new Dictionary<string, ImageMetadata>
        {
            [files[0]] = BuildMetadata(files[0], ImageType.Flat, 1.0),
            [files[1]] = BuildMetadata(files[1], ImageType.Flat, 1.0),
            [files[2]] = BuildMetadata(files[2], ImageType.Flat, 1.0),
            [files[3]] = BuildMetadata(files[3], ImageType.Dark, 1.0),
            [files[4]] = BuildMetadata(files[4], ImageType.Dark, 1.0),
            [files[5]] = BuildMetadata(files[5], ImageType.Dark, 1.0)
        };

        _metadataReader
            .Setup(m => m.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> paths, CancellationToken _) =>
            {
                var result = new Dictionary<string, ImageMetadata>();
                foreach (var path in paths)
                {
                    if (metadata.TryGetValue(path, out var meta))
                        result[path] = meta;
                }

                return result;
            });

        var jobs = await _scanner.ScanFlatDirectoriesAsync(new[] { _tempRoot });

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().ContainSingle();
        jobs[0].ExposureGroups[0].FilePaths.Should().HaveCount(3);
        jobs[0].ExposureGroups[0].FilePaths.Should().OnlyContain(p => Path.GetFileName(p).StartsWith("flat_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_DoesNotDropWbppMasterFlatNames()
    {
        var files = new[]
        {
            CreateFile("masterFlat_BIN-1_9576x6388_FILTER-Blue_mono.xisf"),
            CreateFile("masterFlat_BIN-1_9576x6388_FILTER-Green_mono.xisf"),
            CreateFile("masterFlat_BIN-1_9576x6388_FILTER-Red_mono.xisf")
        };

        var metadata = files.ToDictionary(
            path => path,
            path => BuildMetadata(path, ImageType.MasterFlat, 5.0));

        _metadataReader
            .Setup(m => m.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> paths, CancellationToken _) =>
            {
                var result = new Dictionary<string, ImageMetadata>();
                foreach (var path in paths)
                {
                    if (metadata.TryGetValue(path, out var meta))
                        result[path] = meta;
                }

                return result;
            });

        var jobs = await _scanner.ScanFlatDirectoriesAsync(new[] { _tempRoot });

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().ContainSingle();
        jobs[0].ExposureGroups[0].FilePaths.Should().HaveCount(3);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_ParsesUnknownFlatFramesWithExposure()
    {
        var files = new[]
        {
            CreateFile("u_001.fits"),
            CreateFile("u_002.fits"),
            CreateFile("u_003.fits")
        };

        var metadata = files.ToDictionary(
            path => path,
            path => BuildMetadata(path, ImageType.Unknown, 2.5));

        _metadataReader
            .Setup(m => m.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> paths, CancellationToken _) =>
            {
                var result = new Dictionary<string, ImageMetadata>();
                foreach (var path in paths)
                {
                    if (metadata.TryGetValue(path, out var meta))
                        result[path] = meta;
                }

                return result;
            });

        var jobs = await _scanner.ScanFlatDirectoriesAsync(new[] { _tempRoot });

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().ContainSingle();
        jobs[0].ExposureGroups[0].ExposureTime.Should().Be(2.5);
        jobs[0].ExposureGroups[0].FilePaths.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("MasterFlat_previous.xisf", ImageType.MasterFlat, null)]
    [InlineData("only_flat.fits", ImageType.Flat, 0.25)]
    public async Task ScanFlatDirectoriesAsync_PreservesSingleImageDirectory(
        string fileName,
        ImageType imageType,
        double? exposure)
    {
        var onlyImage = CreateFile(fileName);
        SetupMetadata(new Dictionary<string, ImageMetadata>
        {
            [onlyImage] = BuildMetadata(onlyImage, imageType, exposure)
        });

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        if (imageType == ImageType.Flat)
        {
            jobs[0].ExposureGroups.Should().ContainSingle();
            jobs[0].ExposureGroups[0].FilePaths.Should().Equal(onlyImage);
        }
        else
        {
            jobs[0].ExposureGroups.Should().BeEmpty();
        }
        jobs[0].PassthroughFiles.Should().Equal(onlyImage);
        jobs[0].TotalFileCount.Should().Be(1);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_PreservesSingleFrameGroupInsideMixedDirectory()
    {
        var integrationFiles = Enumerable.Range(1, 3)
            .Select(i => CreateFile($"flat_1s_{i:000}.fits"))
            .ToArray();
        var loneFlat = CreateFile("flat_5s_only.fits");
        var metadata = integrationFiles
            .Select(path => BuildMetadata(path, ImageType.Flat, 1.0))
            .Append(BuildMetadata(loneFlat, ImageType.Flat, 5.13))
            .ToDictionary(item => item.FilePath);
        SetupMetadata(metadata);

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().HaveCount(2);
        jobs[0].ExposureGroups.Single(group => group.ExposureTime == 1.0)
            .FilePaths.Should().BeEquivalentTo(integrationFiles);
        jobs[0].ExposureGroups.Single(group => group.ExposureTime == 5.13)
            .FilePaths.Should().Equal(loneFlat);
        jobs[0].PassthroughFiles.Should().Equal(loneFlat);
        jobs[0].TotalFileCount.Should().Be(4);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_MarksTwoFrameGroupAsPreservationCandidate()
    {
        var integrationFiles = Enumerable.Range(1, 3)
            .Select(i => CreateFile($"flat_1s_{i:000}.fits"))
            .ToArray();
        var twoFrameGroup = Enumerable.Range(1, 2)
            .Select(i => CreateFile($"flat_5s_{i:000}.fits"))
            .ToArray();
        var metadata = integrationFiles
            .Select(path => BuildMetadata(path, ImageType.Flat, 1.0))
            .Concat(twoFrameGroup.Select(path => BuildMetadata(path, ImageType.Flat, 5.13)))
            .ToDictionary(item => item.FilePath);
        SetupMetadata(metadata);

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().HaveCount(2);
        jobs[0].PassthroughFiles.Should().BeEquivalentTo(twoFrameGroup);
        jobs[0].TotalFileCount.Should().Be(5);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_PreservesSingleImageWhenMetadataCannotBeRead()
    {
        var onlyImage = CreateFile("unreadable_master.xisf");
        SetupMetadata(new Dictionary<string, ImageMetadata>());

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().BeEmpty();
        jobs[0].PassthroughFiles.Should().Equal(onlyImage);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_SeparatesSameExposureByCalibrationIdentity()
    {
        var redFiles = Enumerable.Range(1, 3).Select(i => CreateFile($"red_{i:000}.fits")).ToArray();
        var blueFiles = Enumerable.Range(1, 3).Select(i => CreateFile($"blue_{i:000}.fits")).ToArray();
        var metadata = redFiles
            .Select(path => BuildMetadata(path, ImageType.Flat, 1.0, "R", "1", 100, 20))
            .Concat(blueFiles.Select(path => BuildMetadata(path, ImageType.Flat, 1.0, "B", "1", 100, 20)))
            .ToDictionary(m => m.FilePath);

        SetupMetadata(metadata);

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().HaveCount(2);
        jobs[0].ExposureGroups.Should().OnlyContain(g => g.FilePaths.Count == 3);
        jobs[0].ExposureGroups.Select(g => g.RepresentativeMetadata!.Filter).Should().BeEquivalentTo(["R", "B"]);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_SeparatesSameMetadataByImageGeometry()
    {
        var fullSize = Enumerable.Range(1, 3).Select(i => CreateFile($"full_{i:000}.fits")).ToArray();
        var roi = Enumerable.Range(1, 3).Select(i => CreateFile($"roi_{i:000}.fits")).ToArray();
        var metadata = fullSize
            .Select(path => BuildMetadata(path, ImageType.Flat, 20.0, "R", "1", 100, 20, 9576, 6388))
            .Concat(roi.Select(path => BuildMetadata(path, ImageType.Flat, 20.0, "R", "1", 100, 20, 1936, 1096)))
            .ToDictionary(m => m.FilePath);

        SetupMetadata(metadata);

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().HaveCount(2);
        jobs[0].ExposureGroups.Select(group => (group.MatchingCriteria!.Width, group.MatchingCriteria.Height))
            .Should().BeEquivalentTo([(9576, 6388), (1936, 1096)]);
    }

    [Fact]
    public async Task ScanFlatDirectoriesAsync_ExcludesFlatMasterGeneratedOutput()
    {
        var rawFiles = Enumerable.Range(1, 3).Select(i => CreateFile($"flat_{i:000}.fits")).ToArray();
        var generatedMaster = CreateFile("MasterFlat_2026-08-07_R_1s.xisf");
        var metadata = rawFiles
            .Select(path => BuildMetadata(path, ImageType.Flat, 1.0, "R"))
            .Append(BuildMetadata(generatedMaster, ImageType.MasterFlat, 1.0, "R"))
            .ToDictionary(m => m.FilePath);

        SetupMetadata(metadata);

        var jobs = await _scanner.ScanFlatDirectoriesAsync([_tempRoot]);

        jobs.Should().ContainSingle();
        jobs[0].ExposureGroups.Should().ContainSingle();
        jobs[0].ExposureGroups[0].FilePaths.Should().BeEquivalentTo(rawFiles);
    }

    [Fact]
    public async Task ScanDarkLibraryAsync_IncludesBiasWithoutExposureAsZero()
    {
        var biasFile = CreateFile("masterbias_001.xisf");

        _metadataReader
            .Setup(m => m.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageMetadata>
            {
                [biasFile] = BuildMetadata(biasFile, ImageType.MasterBias, null)
            });

        var darks = await _scanner.ScanDarkLibraryAsync(new[] { _tempRoot });

        darks.Should().ContainSingle();
        darks[0].Type.Should().Be(ImageType.MasterBias);
        darks[0].ExposureTime.Should().Be(0.0);
    }

    [Fact]
    public async Task ScanDarkLibraryAsync_FindsSpacedMasterBiasNameInsideSkippedMastersFolder()
    {
        var mastersFolder = Path.Combine(_tempRoot, "Masters");
        Directory.CreateDirectory(mastersFolder);
        var biasFile = Path.Combine(mastersFolder, "Master Bias 100 gain.xisf");
        File.WriteAllText(biasFile, string.Empty);

        SetupMetadata(new Dictionary<string, ImageMetadata>
        {
            [biasFile] = BuildMetadata(biasFile, ImageType.MasterBias, null, binning: "1", gain: 100)
        });

        var darks = await _scanner.ScanDarkLibraryAsync([_tempRoot]);

        darks.Should().ContainSingle();
        darks[0].FilePath.Should().Be(biasFile);
        darks[0].Type.Should().Be(ImageType.MasterBias);
        darks[0].ExposureTime.Should().Be(0.0);
    }

    [Fact]
    public async Task ScanDarkLibraryAsync_BackfillsMasterTemperatureOnlyFromMatchingSiblingPool()
    {
        var firstPool = Path.Combine(_tempRoot, "DARKS100");
        var secondPool = Path.Combine(_tempRoot, "DARKS200");
        Directory.CreateDirectory(firstPool);
        Directory.CreateDirectory(secondPool);

        var master = Path.Combine(firstPool, "masterdark.xisf");
        var sibling = Path.Combine(firstPool, "dark_001.fits");
        var unrelated = Path.Combine(secondPool, "dark_002.fits");
        File.WriteAllText(master, string.Empty);
        File.WriteAllText(sibling, string.Empty);
        File.WriteAllText(unrelated, string.Empty);

        var metadata = new[]
        {
            BuildMetadata(master, ImageType.MasterDark, 25, binning: "1", gain: 100, offset: 20),
            BuildMetadata(sibling, ImageType.Dark, 25, binning: "1", gain: 100, offset: 20) with { Temperature = -10 },
            BuildMetadata(unrelated, ImageType.Dark, 25, binning: "1", gain: 100, offset: 20) with { Temperature = 5 }
        }.ToDictionary(item => item.FilePath);
        SetupMetadata(metadata);

        var darks = await _scanner.ScanDarkLibraryAsync([_tempRoot]);

        darks.Single(item => item.FilePath == master).Temperature.Should().Be(-10);
    }

    private string CreateFile(string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private void SetupMetadata(IReadOnlyDictionary<string, ImageMetadata> metadata)
    {
        _metadataReader
            .Setup(m => m.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> paths, CancellationToken _) => paths
                .Where(metadata.ContainsKey)
                .ToDictionary(path => path, path => metadata[path]));
    }

    private static ImageMetadata BuildMetadata(
        string path,
        ImageType type,
        double? exposure,
        string? filter = null,
        string? binning = null,
        double? gain = null,
        double? offset = null,
        int width = 0,
        int height = 0,
        int channels = 1)
    {
        return new ImageMetadata
        {
            FilePath = path,
            Type = type,
            ExposureTime = exposure,
            Filter = filter,
            Binning = binning,
            Gain = gain,
            Offset = offset,
            Width = width,
            Height = height,
            Channels = channels
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup for temporary test files.
        }
    }
}
