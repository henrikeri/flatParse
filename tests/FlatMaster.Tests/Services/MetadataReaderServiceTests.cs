using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlatMaster.Core.Configuration;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class MetadataReaderServiceTests
{
    [Fact]
    public async Task ReadMetadataAsync_SpacedMasterBiasFilename_FallsBackToMasterBiasType()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Master Bias 100 gain {Guid.NewGuid():N}.xisf");
        try
        {
            File.WriteAllText(path, string.Empty);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new MetadataReaderService(
                NullLogger<MetadataReaderService>.Instance,
                cache,
                Options.Create(new MetadataReaderOptions { UseMemoryCache = false }));

            var metadata = await service.ReadMetadataAsync(path);

            metadata.Should().NotBeNull();
            metadata!.Type.Should().Be(FlatMaster.Core.Models.ImageType.MasterBias);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadMetadataAsync_MasterBiasFilename_WinsOverContradictoryMasterDarkHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MasterBias_{Guid.NewGuid():N}.xisf");
        try
        {
            var image = new FitsImageIO.ImageData
            {
                Width = 3,
                Height = 3,
                Channels = 1,
                Pixels = Enumerable.Repeat(0.25, 9).ToArray(),
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["IMAGETYP"] = "Master Dark"
                }
            };
            await FitsImageIO.WriteXisfAsync(path, image);

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new MetadataReaderService(
                NullLogger<MetadataReaderService>.Instance,
                cache,
                Options.Create(new MetadataReaderOptions { UseMemoryCache = false }));

            var metadata = await service.ReadMetadataAsync(path);

            metadata.Should().NotBeNull();
            metadata!.Type.Should().Be(FlatMaster.Core.Models.ImageType.MasterBias);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("fits", 17, 11, 1)]
    [InlineData("xisf", 13, 7, 3)]
    public async Task ReadMetadataAsync_ExtractsImageGeometry(
        string extension,
        int width,
        int height,
        int channels)
    {
        var path = Path.Combine(Path.GetTempPath(), $"flatmaster_metadata_{Guid.NewGuid():N}.{extension}");
        try
        {
            var image = new FitsImageIO.ImageData
            {
                Width = width,
                Height = height,
                Channels = channels,
                Pixels = Enumerable.Repeat(0.25, width * height * channels).ToArray()
            };
            if (extension == "fits")
                await FitsImageIO.WriteFitsAsync(path, image);
            else
                await FitsImageIO.WriteXisfAsync(path, image);

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new MetadataReaderService(
                NullLogger<MetadataReaderService>.Instance,
                cache,
                Options.Create(new MetadataReaderOptions { UseMemoryCache = false }));

            var metadata = await service.ReadMetadataAsync(path);

            metadata.Should().NotBeNull();
            metadata!.Width.Should().Be(width);
            metadata.Height.Should().Be(height);
            metadata.Channels.Should().Be(channels);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
