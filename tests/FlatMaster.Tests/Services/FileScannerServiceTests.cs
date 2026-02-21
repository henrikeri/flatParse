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

    private string CreateFile(string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static ImageMetadata BuildMetadata(string path, ImageType type, double? exposure)
    {
        return new ImageMetadata
        {
            FilePath = path,
            Type = type,
            ExposureTime = exposure
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
