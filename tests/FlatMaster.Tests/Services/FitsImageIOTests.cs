using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlatMaster.Tests.Services;

public class FitsImageIOTests
{
    [Fact]
    public async Task WriteXisfAsync_Grayscale_WritesExplicitChannelDimension()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"flatmaster_{Guid.NewGuid():N}.xisf");
        try
        {
            var image = new FitsImageIO.ImageData
            {
                Width = 10,
                Height = 6,
                Channels = 1,
                Pixels = Enumerable.Repeat(0.5, 10 * 6).ToArray(),
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            await FitsImageIO.WriteXisfAsync(tempPath, image);
            var xml = ReadXisfHeaderXml(tempPath);

            xml.Should().Contain("geometry=\"10:6:1\"");
            xml.Should().Contain("colorSpace=\"Gray\"");
            xml.Should().Contain("sampleFormat=\"Float64\"");
            xml.Should().Contain("bounds=\"0:1\"");

            var io = new FitsImageIO(NullLogger.Instance);
            var roundTrip = await io.ReadAsync(tempPath);
            roundTrip.Width.Should().Be(10);
            roundTrip.Height.Should().Be(6);
            roundTrip.Channels.Should().Be(1);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task WriteXisfAsync_MultiChannel_WritesRgbColorSpace()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"flatmaster_{Guid.NewGuid():N}.xisf");
        try
        {
            var image = new FitsImageIO.ImageData
            {
                Width = 4,
                Height = 3,
                Channels = 3,
                Pixels = Enumerable.Repeat(0.25, 4 * 3 * 3).ToArray(),
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            await FitsImageIO.WriteXisfAsync(tempPath, image);
            var xml = ReadXisfHeaderXml(tempPath);

            xml.Should().Contain("geometry=\"4:3:3\"");
            xml.Should().Contain("colorSpace=\"RGB\"");
            xml.Should().Contain("sampleFormat=\"Float64\"");
            xml.Should().Contain("bounds=\"0:1\"");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ReadXisfAsync_CompressedZlibByteShuffleUInt16_DecodesCorrectly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"flatmaster_{Guid.NewGuid():N}_compressed.xisf");
        try
        {
            var samples = new ushort[] { 100, 200, 300, 400, 500, 600 };
            WriteCompressedUInt16Xisf(tempPath, 3, 2, samples);

            var io = new FitsImageIO(NullLogger.Instance);
            var image = await io.ReadAsync(tempPath);

            image.Width.Should().Be(3);
            image.Height.Should().Be(2);
            image.Channels.Should().Be(1);
            image.Pixels.Should().HaveCount(samples.Length);

            for (var i = 0; i < samples.Length; i++)
                image.Pixels[i].Should().BeApproximately(samples[i] / 65535.0, 1e-12);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string ReadXisfHeaderXml(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> sig = stackalloc byte[16];
        fs.ReadExactly(sig);

        var magic = Encoding.ASCII.GetString(sig[..8]);
        magic.Should().Be("XISF0100");

        var headerLen = BinaryPrimitives.ReadUInt32LittleEndian(sig[8..12]);
        var headerBytes = new byte[headerLen];
        fs.ReadExactly(headerBytes);
        return Encoding.UTF8.GetString(headerBytes);
    }

    private static void WriteCompressedUInt16Xisf(string path, int width, int height, IReadOnlyList<ushort> samples)
    {
        var pixelBytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixelBytes.AsSpan(i * 2, 2), samples[i]);

        var shuffled = ShuffleBytes(pixelBytes, 2);

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(shuffled, 0, shuffled.Length);
            compressed = ms.ToArray();
        }

        var geom = $"{width}:{height}:1";
        var compression = $"zlib+sh:{pixelBytes.Length}:2";

        string BuildXml(long offset) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<Image geometry=\"{geom}\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"attachment:{offset}:{compressed.Length}\" compression=\"{compression}\">" +
            "</Image></xisf>";

        var first = Encoding.UTF8.GetBytes(BuildXml(0));
        var headerBlockSize = ((first.Length + 4095) / 4096) * 4096;
        var offset = 16 + headerBlockSize;

        var second = Encoding.UTF8.GetBytes(BuildXml(offset));
        headerBlockSize = ((second.Length + 4095) / 4096) * 4096;
        offset = 16 + headerBlockSize;

        second = Encoding.UTF8.GetBytes(BuildXml(offset));
        headerBlockSize = ((second.Length + 4095) / 4096) * 4096;

        var paddedHeader = new byte[headerBlockSize];
        Array.Copy(second, paddedHeader, second.Length);
        for (var i = second.Length; i < paddedHeader.Length; i++)
            paddedHeader[i] = 0x20;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> sig = stackalloc byte[16];
        Encoding.ASCII.GetBytes("XISF0100", sig[..8]);
        BinaryPrimitives.WriteUInt32LittleEndian(sig[8..12], (uint)headerBlockSize);
        fs.Write(sig);
        fs.Write(paddedHeader, 0, paddedHeader.Length);
        fs.Write(compressed, 0, compressed.Length);
    }

    private static byte[] ShuffleBytes(byte[] input, int itemSize)
    {
        var itemCount = input.Length / itemSize;
        var output = new byte[input.Length];
        for (var b = 0; b < itemSize; b++)
        {
            var dst = b * itemCount;
            for (var i = 0; i < itemCount; i++)
                output[dst + i] = input[i * itemSize + b];
        }

        return output;
    }
}
