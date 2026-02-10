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

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Low-level FITS and XISF pixel data reader/writer.
/// Supports 16-bit unsigned integer and 32/64-bit IEEE float pixel buffers.
/// </summary>
public sealed class FitsImageIO
{
    private readonly ILogger _logger;

    public FitsImageIO(ILogger logger)
    {
        _logger = logger;
    }

    // ───────────────────── Data Structures ─────────────────────

    public sealed class ImageData
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public int Channels { get; init; } = 1;
        /// <summary>Pixel buffer in row-major order, normalised to [0,1] as double.</summary>
        public required double[] Pixels { get; init; }
        /// <summary>Preserved FITS header cards for round-tripping.</summary>
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ───────────────────── FITS Reading ─────────────────────

    public async Task<ImageData> ReadAsync(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xisf" => await ReadXisfAsync(path, ct),
            _ => await ReadFitsAsync(path, ct) // .fits, .fit
        };
    }

    private async Task<ImageData> ReadFitsAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Parse header blocks (2880 bytes each, 36 cards of 80 chars) ──
        var block = new byte[2880];
        bool endFound = false;
        while (!endFound)
        {
            int read = await ReadExactAsync(fs, block, ct);
            if (read < 2880) throw new InvalidDataException($"Truncated FITS header in {path}");

            for (int i = 0; i < 36; i++)
            {
                var card = Encoding.ASCII.GetString(block, i * 80, 80);
                if (card.StartsWith("END ") || card.TrimEnd() == "END")
                {
                    endFound = true;
                    break;
                }
                var eq = card.IndexOf('=');
                if (eq > 0 && eq < 9)
                {
                    var key = card[..eq].Trim();
                    var rest = card[(eq + 1)..];
                    var slash = rest.IndexOf('/');
                    var val = (slash > 0 ? rest[..slash] : rest).Trim().Trim('\'', ' ');
                    if (!string.IsNullOrEmpty(key)) headers[key] = val;
                }
            }
        }

        // ── Extract geometry ──
        int bitpix = int.Parse(headers.GetValueOrDefault("BITPIX", "16"), CultureInfo.InvariantCulture);
        int naxis = int.Parse(headers.GetValueOrDefault("NAXIS", "2"), CultureInfo.InvariantCulture);
        int width = int.Parse(headers.GetValueOrDefault("NAXIS1", "0"), CultureInfo.InvariantCulture);
        int height = int.Parse(headers.GetValueOrDefault("NAXIS2", "0"), CultureInfo.InvariantCulture);
        int channels = naxis >= 3 ? int.Parse(headers.GetValueOrDefault("NAXIS3", "1"), CultureInfo.InvariantCulture) : 1;

        double bzero = double.Parse(headers.GetValueOrDefault("BZERO", "0"), CultureInfo.InvariantCulture);
        double bscale = double.Parse(headers.GetValueOrDefault("BSCALE", "1"), CultureInfo.InvariantCulture);

        if (width == 0 || height == 0)
            throw new InvalidDataException($"Invalid FITS dimensions {width}x{height} in {path}");

        // ── Align to 2880 boundary ── (we already consumed full blocks)
        int bytesPerPixel = Math.Abs(bitpix) / 8;
        long pixelCount = (long)width * height * channels;
        long dataBytes = pixelCount * bytesPerPixel;

        var rawBuf = new byte[dataBytes];
        int dataRead = await ReadExactAsync(fs, rawBuf, ct);
        if (dataRead < dataBytes)
            _logger.LogWarning("FITS pixel data truncated: expected {Expected}, got {Got}", dataBytes, dataRead);

        // ── Decode pixels to double [0,1] ──
        var pixels = new double[pixelCount];
        DecodePixels(rawBuf, pixels, bitpix, bzero, bscale);

        return new ImageData
        {
            Width = width,
            Height = height,
            Channels = channels,
            Pixels = pixels,
            Headers = headers
        };
    }

    // ───────────────────── XISF Reading ─────────────────────

    private async Task<ImageData> ReadXisfAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

        // XISF signature: "XISF0100" (8 bytes) then 4-byte LE header length, 4 reserved
        var sig = new byte[16];
        await ReadExactAsync(fs, sig, ct);
        var magic = Encoding.ASCII.GetString(sig, 0, 8);
        if (!magic.StartsWith("XISF"))
            throw new InvalidDataException($"Not a valid XISF file: {path}");

        uint headerLen = BinaryPrimitives.ReadUInt32LittleEndian(sig.AsSpan(8));
        var headerBuf = new byte[headerLen];
        await ReadExactAsync(fs, headerBuf, ct);
        var xml = Encoding.UTF8.GetString(headerBuf);

        // Parse geometry from XML: <Image geometry="W:H:C" sampleFormat="Float32" location="...">
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract FITSKeywords — strip single quotes (XISF wraps strings like value="'FLAT'")
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(xml,
                @"<FITSKeyword\s+(?:name|keyword)=""([^""]+)""\s+value=""([^""]*)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            headers[m.Groups[1].Value] = m.Groups[2].Value.Trim('\'', ' ');
        }

        // Parse Image element
        var imgMatch = System.Text.RegularExpressions.Regex.Match(xml,
            @"<Image\s[^>]*geometry=""(\d+):(\d+):?(\d+)?""[^>]*sampleFormat=""([^""]+)""[^>]*location=""attachment:(\d+):(\d+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!imgMatch.Success)
            throw new InvalidDataException($"Cannot parse XISF Image element in {path}");

        int width = int.Parse(imgMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        int height = int.Parse(imgMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        int channels = imgMatch.Groups[3].Success && imgMatch.Groups[3].Value.Length > 0
            ? int.Parse(imgMatch.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
        string sampleFormat = imgMatch.Groups[4].Value;
        long attachOffset = long.Parse(imgMatch.Groups[5].Value, CultureInfo.InvariantCulture);
        long attachLen = long.Parse(imgMatch.Groups[6].Value, CultureInfo.InvariantCulture);

        int bytesPerSample = sampleFormat.ToLowerInvariant() switch
        {
            "float32" => 4,
            "float64" => 8,
            "uint16" => 2,
            "uint8" => 1,
            "uint32" => 4,
            _ => throw new NotSupportedException($"Unsupported XISF sample format: {sampleFormat}")
        };

        // Seek to attachment — XISF attachment offset is absolute from file start
        fs.Seek(attachOffset, SeekOrigin.Begin);

        long pixelCount = (long)width * height * channels;
        var rawBuf = new byte[pixelCount * bytesPerSample];
        await ReadExactAsync(fs, rawBuf, ct);

        var pixels = new double[pixelCount];
        DecodeXisfPixels(rawBuf, pixels, sampleFormat);

        return new ImageData
        {
            Width = width,
            Height = height,
            Channels = channels,
            Pixels = pixels,
            Headers = headers
        };
    }

    // ───────────────────── XISF Writing ─────────────────────

    public async Task WriteXisfAsync(string path, ImageData image, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        int channels = image.Channels;

        // Build XML header — two-pass: first build XML to measure size, then fix attachment offset
        long pixelCount = (long)image.Width * image.Height * channels;
        long dataBytes = pixelCount * 4; // Float32

        var geom = channels > 1 ? $"{image.Width}:{image.Height}:{channels}" : $"{image.Width}:{image.Height}";

        // We need the final header block size to compute the absolute attachment offset.
        // Build a template, measure, round up to 4096, then rebuild with correct offset.
        string BuildXml(long attachmentOffset)
        {
            var sb2 = new StringBuilder();
            sb2.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb2.Append("<xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb2.Append($"<Image geometry=\"{geom}\" sampleFormat=\"Float32\" " +
                      $"colorSpace=\"Gray\" location=\"attachment:{attachmentOffset}:{dataBytes}\">");

            foreach (var kvp in image.Headers)
            {
                var escaped = System.Security.SecurityElement.Escape(kvp.Value) ?? "";
                sb2.Append($"<FITSKeyword name=\"{kvp.Key}\" value=\"{escaped}\" comment=\"\"/>");
            }
            sb2.Append("</Image></xisf>");
            return sb2.ToString();
        }

        // First pass: estimate header size with placeholder offset
        var estimateXml = BuildXml(0);
        var estimateBytes = Encoding.UTF8.GetBytes(estimateXml);
        int headerBlockSize = ((estimateBytes.Length + 4095) / 4096) * 4096;
        long actualAttachmentOffset = 16 + headerBlockSize; // absolute offset from file start

        // Second pass: rebuild with correct offset
        var finalXml = BuildXml(actualAttachmentOffset);
        var xmlBytes = Encoding.UTF8.GetBytes(finalXml);
        // Recalculate in case digit count changed the length
        headerBlockSize = ((xmlBytes.Length + 4095) / 4096) * 4096;
        actualAttachmentOffset = 16 + headerBlockSize;
        // One more pass if the offset changed the block size
        if (Encoding.UTF8.GetByteCount(BuildXml(actualAttachmentOffset)) > xmlBytes.Length)
        {
            finalXml = BuildXml(actualAttachmentOffset);
            xmlBytes = Encoding.UTF8.GetBytes(finalXml);
            headerBlockSize = ((xmlBytes.Length + 4095) / 4096) * 4096;
        }

        var paddedHeader = new byte[headerBlockSize];
        Array.Copy(xmlBytes, paddedHeader, xmlBytes.Length);
        // Fill remaining with spaces
        for (int i = xmlBytes.Length; i < headerBlockSize; i++) paddedHeader[i] = 0x20;

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);

        // Signature: "XISF0100" + 4-byte LE header length + 4 reserved
        var sigBuf = new byte[16];
        Encoding.ASCII.GetBytes("XISF0100", 0, 8, sigBuf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sigBuf.AsSpan(8), (uint)headerBlockSize);
        await fs.WriteAsync(sigBuf, ct);

        // Header
        await fs.WriteAsync(paddedHeader, ct);

        // Pixel data as Float32 LE
        var dataBuf = new byte[dataBytes];
        for (long i = 0; i < pixelCount; i++)
        {
            float val = (float)image.Pixels[i];
            BinaryPrimitives.WriteSingleLittleEndian(dataBuf.AsSpan((int)(i * 4)), val);
        }
        await fs.WriteAsync(dataBuf, ct);
    }

    // ───────────────────── FITS Writing ─────────────────────

    public async Task WriteFitsAsync(string path, ImageData image, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        var cards = new List<string>();

        void AddCard(string key, string value, string comment = "")
        {
            var card = $"{key,-8}= {value,20}";
            if (!string.IsNullOrEmpty(comment)) card += $" / {comment}";
            cards.Add(card.PadRight(80)[..80]);
        }

        AddCard("SIMPLE", "T", "FITS standard");
        AddCard("BITPIX", "-32", "32-bit IEEE float");
        int naxis = image.Channels > 1 ? 3 : 2;
        AddCard("NAXIS", naxis.ToString(), "Number of axes");
        AddCard("NAXIS1", image.Width.ToString(), "Width");
        AddCard("NAXIS2", image.Height.ToString(), "Height");
        if (image.Channels > 1)
            AddCard("NAXIS3", image.Channels.ToString(), "Channels");

        // Replicate known headers
        foreach (var kvp in image.Headers)
        {
            var k = kvp.Key.ToUpperInvariant();
            if (k is "SIMPLE" or "BITPIX" or "NAXIS" or "NAXIS1" or "NAXIS2" or "NAXIS3" or "END") continue;
            var val = kvp.Value;
            // Try to preserve numeric values without quotes
            if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                AddCard(kvp.Key, val);
            else
                AddCard(kvp.Key, $"'{val}'");
        }

        cards.Add("END".PadRight(80));

        // Pad to 2880 boundary
        while (cards.Count % 36 != 0)
            cards.Add(new string(' ', 80));

        var headerBytes = Encoding.ASCII.GetBytes(string.Concat(cards));
        await fs.WriteAsync(headerBytes, ct);

        // Pixel data: IEEE float -32, big-endian
        long pixelCount = (long)image.Width * image.Height * image.Channels;
        var dataBuf = new byte[pixelCount * 4];
        for (long i = 0; i < pixelCount; i++)
        {
            float val = (float)image.Pixels[i];
            BinaryPrimitives.WriteSingleBigEndian(dataBuf.AsSpan((int)(i * 4)), val);
        }
        await fs.WriteAsync(dataBuf, ct);

        // Pad data to 2880 boundary
        int remainder = (int)(dataBuf.Length % 2880);
        if (remainder > 0)
        {
            var pad = new byte[2880 - remainder];
            await fs.WriteAsync(pad, ct);
        }
    }

    // ───────────────────── Decode helpers ─────────────────────

    private static void DecodePixels(byte[] raw, double[] output, int bitpix, double bzero, double bscale)
    {
        // FITS is big-endian
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < output.Length; i++)
                    output[i] = (raw[i] * bscale + bzero) / 255.0;
                break;

            case 16:
                // Signed 16-bit, but with BZERO=32768 => unsigned
                for (int i = 0; i < output.Length; i++)
                {
                    short val = BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(i * 2));
                    output[i] = (val * bscale + bzero) / 65535.0;
                }
                break;

            case 32:
                for (int i = 0; i < output.Length; i++)
                {
                    int val = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(i * 4));
                    output[i] = val * bscale + bzero;
                }
                break;

            case -32: // IEEE 754 single
                for (int i = 0; i < output.Length; i++)
                {
                    double v = BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(i * 4));
                    output[i] = v * bscale + bzero;
                }
                break;

            case -64: // IEEE 754 double
                for (int i = 0; i < output.Length; i++)
                {
                    double v = BinaryPrimitives.ReadDoubleBigEndian(raw.AsSpan(i * 8));
                    output[i] = v * bscale + bzero;
                }
                break;

            default:
                throw new NotSupportedException($"BITPIX {bitpix} not supported");
        }
    }

    private static void DecodeXisfPixels(byte[] raw, double[] output, string sampleFormat)
    {
        // XISF is little-endian
        switch (sampleFormat.ToLowerInvariant())
        {
            case "uint8":
                for (int i = 0; i < output.Length; i++)
                    output[i] = raw[i] / 255.0;
                break;

            case "uint16":
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)) / 65535.0;
                break;

            case "uint32":
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4)) / 4294967295.0;
                break;

            case "float32":
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
                break;

            case "float64":
                for (int i = 0; i < output.Length; i++)
                    output[i] = BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(i * 8));
                break;

            default:
                throw new NotSupportedException($"XISF sample format '{sampleFormat}' not supported");
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (n == 0) break;
            totalRead += n;
        }
        return totalRead;
    }
}

