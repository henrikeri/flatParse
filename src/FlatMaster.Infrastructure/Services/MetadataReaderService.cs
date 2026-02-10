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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using FlatMaster.Core.Configuration;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Reads metadata from FITS and XISF files
/// </summary>
public sealed partial class MetadataReaderService : IMetadataReaderService
{
    private readonly ILogger<MetadataReaderService> _logger;
    private readonly IMemoryCache _cache;
    private readonly MetadataReaderOptions _options;
    private const int MaxHeaderBytes = 4 * 1024 * 1024;
    
    // Regex patterns for filename exposure inference
    [GeneratedRegex(@"(?<![A-Za-z])(\d+(?:\.\d+)?)\s*s(?=[_\-\.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExposurePattern1();
    
    [GeneratedRegex(@"EXPOSURE[_\-=:\s]?(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ExposurePattern2();

    // Regex pattern for filename temperature inference (e.g. temp_-10.00 or temp_20.5)
    [GeneratedRegex(@"temp[_\-=\s](-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TempPattern();
    
    [GeneratedRegex(@"</\s*(?:\w+:)?XISF\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex XisfClosePattern();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fits", ".fit", ".xisf"
    };

    private static readonly string[] ExposureKeys = { "EXPTIME", "EXPOSURE", "EXPOSURETIME", "X_EXPOSURE" };
    private static readonly string[] BinningKeys = { "XBINNING", "BINNING", "CCDBINNING", "BINNING_MODE" };
    private static readonly string[] GainKeys = { "GAIN", "EGAIN" };
    private static readonly string[] OffsetKeys = { "OFFSET", "BLACKLEVEL" };
    private static readonly string[] TempKeys = { "CCD-TEMP", "CCD_TEMP", "SENSOR_TEMP", "SENSOR-TEMP", "SET-TEMP", "SET_TEMP" };
    private static readonly string[] FilterKeys = { "FILTER", "INSFLNAM" };
    private static readonly string[] DateKeys = { "DATE-OBS", "DATE_OBS", "DATE" };
    private static readonly string[] ImageTypeKeys = { "IMAGETYP", "FRAMETYPE", "FRAME" };

    public MetadataReaderService(
        ILogger<MetadataReaderService> logger,
        IMemoryCache cache,
        IOptions<MetadataReaderOptions> options)
    {
        _logger = logger;
        _cache = cache;
        _options = options.Value;
    }

    public bool IsSupportedFormat(string filePath) 
        => SupportedExtensions.Contains(Path.GetExtension(filePath));

    public async Task<ImageMetadata?> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found: {FilePath}", filePath);
            return null;
        }

        if (_options.UseMemoryCache)
        {
            var cacheKey = GetCacheKey(filePath);
            if (_cache.TryGetValue(cacheKey, out ImageMetadata? cached))
            {
                return cached;
            }
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        try
        {
            var metadata = extension switch
            {
                ".fits" or ".fit" => await ReadFitsMetadataAsync(filePath, cancellationToken),
                ".xisf" => await ReadXisfMetadataAsync(filePath, cancellationToken),
                _ => null
            };

            if (metadata != null && _options.UseMemoryCache)
            {
                var cacheKey = GetCacheKey(filePath);
                var entryOptions = new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromHours(6)
                };

                if (_options.CacheSizeLimitEntries > 0)
                {
                    entryOptions.Size = 1;
                }

                _cache.Set(cacheKey, metadata, entryOptions);
            }

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading metadata from {FilePath}", filePath);
            return CreateFallbackMetadata(filePath);
        }
    }

    public async Task<Dictionary<string, ImageMetadata>> ReadMetadataBatchAsync(
        IEnumerable<string> filePaths, 
        CancellationToken cancellationToken = default)
    {
        var fileList = filePaths.ToList();
        _logger.LogDebug("Reading metadata for {Count} files", fileList.Count);
        
        var results = new ConcurrentDictionary<string, ImageMetadata>(StringComparer.OrdinalIgnoreCase);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism)
        };

        var failed = new ConcurrentBag<string>();
        
        await Parallel.ForEachAsync(fileList, parallelOptions, async (path, ct) =>
        {
            var metadata = await ReadMetadataAsync(path, ct);
            if (metadata != null)
            {
                results[path] = metadata;
            }
            else
            {
                failed.Add(path);
            }
        });

        if (failed.Count > 0)
        {
            _logger.LogWarning("Failed to read metadata from {Count} files (first 5): {Files}", 
                failed.Count, string.Join(", ", failed.Take(5).Select(Path.GetFileName)));
        }
        
        _logger.LogDebug("Successfully read metadata for {Success}/{Total} files", results.Count, fileList.Count);

        return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static string GetCacheKey(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            return $"{filePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return filePath;
        }
    }

    private async Task<ImageMetadata?> ReadFitsMetadataAsync(string filePath, CancellationToken cancellationToken)
    {
        var headers = await ParseFitsHeadersAsync(filePath, cancellationToken);
        _logger.LogDebug("FITS: {File} - Found {Count} headers: {Headers}", 
            Path.GetFileName(filePath), headers.Count, 
            headers.Count > 0 ? string.Join(", ", headers.Keys.Take(5)) + (headers.Count > 5 ? "..." : "") : "none");
        return CreateMetadataFromHeaders(filePath, headers);
    }

    private async Task<ImageMetadata?> ReadXisfMetadataAsync(string filePath, CancellationToken cancellationToken)
    {
        var xmlHeader = await ExtractXisfXmlHeaderAsync(filePath, cancellationToken);
        if (string.IsNullOrEmpty(xmlHeader))
        {
            _logger.LogWarning("XISF: {File} - No XML header found", Path.GetFileName(filePath));
            return CreateFallbackMetadata(filePath);
        }
        
        var headers = ParseXisfXml(xmlHeader);
        _logger.LogDebug("XISF: {File} - Found {Count} headers: {Headers}", 
            Path.GetFileName(filePath), headers.Count,
            headers.Count > 0 ? string.Join(", ", headers.Keys.Take(5)) + (headers.Count > 5 ? "..." : "") : "none");
        return CreateMetadataFromHeaders(filePath, headers);
    }

    private async Task<Dictionary<string, string>> ParseFitsHeadersAsync(string filePath, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(filePath);
        
        _logger.LogDebug("FITS: {File} - Starting to parse FITS headers", fileName);
        
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 2880);
        var buffer = new byte[2880]; // FITS header is in 2880-byte blocks
        
        int blockCount = 0;
        int cardCount = 0;
        
        while (true)
        {
            var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, 2880), cancellationToken);
            _logger.LogDebug("FITS: {File} - Block {Block}: Read {Bytes} bytes (expected 2880)", 
                fileName, blockCount + 1, bytesRead);
            
            if (bytesRead != 2880)
            {
                _logger.LogWarning("FITS: {File} - Incomplete block read: {Bytes} bytes (expected 2880)", 
                    fileName, bytesRead);
                break;
            }
            
            blockCount++;
            
            // Log first few bytes of first block for diagnostics
            if (blockCount == 1)
            {
                var firstCard = Encoding.ASCII.GetString(buffer, 0, Math.Min(80, bytesRead));
                _logger.LogDebug("FITS: {File} - First card: [{Card}]", fileName, firstCard.Trim());
            }
            
            for (int i = 0; i < 36; i++) // 36 cards per block
            {
                var card = Encoding.ASCII.GetString(buffer, i * 80, 80);
                cardCount++;
                
                if (card.StartsWith("END "))
                {
                    _logger.LogDebug("FITS: {File} - END found at block {Block}, card {Card}, {Keywords} keywords extracted",
                        fileName, blockCount, cardCount, headers.Count);
                    return headers;
                }
                
                // Parse "KEYWORD = VALUE / COMMENT" format
                var eqIndex = card.IndexOf('=');
                if (eqIndex > 0 && eqIndex < 9) // Keywords can be up to 8 chars, = at position 8
                {
                    var key = card[..eqIndex].Trim();
                    var rest = card[(eqIndex + 1)..];
                    var slashIndex = rest.IndexOf('/');
                    var value = (slashIndex > 0 ? rest[..slashIndex] : rest).Trim().Trim('\'', ' ');
                    
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        headers[key] = value;
                        if (key.Equals("EXPTIME", StringComparison.OrdinalIgnoreCase) || 
                            key.Equals("EXPOSURE", StringComparison.OrdinalIgnoreCase))
                            _logger.LogDebug("FITS: {File} - Found {Key}={Value}", fileName, key, value);
                    }
                }
            }
            
            // Stop after 100 blocks (safety limit)
            if (blockCount >= 100)
            {
                _logger.LogWarning("FITS: {File} - Reached 100 block limit without END marker", fileName);
                break;
            }
        }
        
        _logger.LogWarning("FITS: {File} - No END marker found (blocks:{Blocks}, cards:{Cards}, keywords:{Keywords})",
            fileName, blockCount, cardCount, headers.Count);
        return headers;
    }

    private async Task<string?> ExtractXisfXmlHeaderAsync(string filePath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(filePath);
        _logger.LogDebug("XISF: {File} - Starting to extract XML header", fileName);
        
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[512 * 1024];
        var sb = new StringBuilder();
        
        int bytesRead;
        int totalBytes = 0;
        while ((bytesRead = await fs.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            
            _logger.LogDebug("XISF: {File} - Read {Bytes} bytes (total: {Total})", fileName, bytesRead, totalBytes);
            
            if (sb.Length > MaxHeaderBytes)
            {
                _logger.LogWarning("XISF: {File} - Exceeded max header size ({Max} bytes)", fileName, MaxHeaderBytes);
                break;
            }
            
            var text = sb.ToString();
            var match = XisfClosePattern().Match(text);
            if (match.Success)
            {
                _logger.LogDebug("XISF: {File} - Found closing tag at position {Pos}", fileName, match.Index);
                return text[..(match.Index + match.Length)];
            }
        }
        
        _logger.LogWarning("XISF: {File} - No closing tag found (read {Total} bytes)", fileName, totalBytes);
        return null;
    }

    private Dictionary<string, string> ParseXisfXml(string xml)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        // Simple XML parsing for FITS keywords and properties
        var fitsKeywordPattern = new Regex(@"<FITSKeyword\s+(?:name|keyword)=""([^""]+)""\s+value=""([^""]+)""", RegexOptions.IgnoreCase);
        var propertyPattern = new Regex(@"<Property\s+id=""([^""]+)""\s+value=""([^""]+)""", RegexOptions.IgnoreCase);
        
        foreach (Match match in fitsKeywordPattern.Matches(xml))
        {
            // XISF stores FITS string values with single quotes, e.g. value="'FLAT'" — strip them
            headers[match.Groups[1].Value] = match.Groups[2].Value.Trim('\'', ' ');
        }
        
        foreach (Match match in propertyPattern.Matches(xml))
        {
            headers[match.Groups[1].Value] = match.Groups[2].Value.Trim('\'', ' ');
        }
        
        return headers;
    }

    private ImageMetadata CreateMetadataFromHeaders(string filePath, Dictionary<string, string> headers)
    {
        double? exposure = TryGetDouble(headers, ExposureKeys);
        if (!exposure.HasValue)
        {
            exposure = InferExposureFromFilename(filePath);
            if (exposure.HasValue)
                _logger.LogDebug("Inferred exposure from filename {File}: {Exposure}s", Path.GetFileName(filePath), exposure);
            else
                _logger.LogWarning("No exposure found for {File} (headers: {HeaderCount}, checked keys: {Keys})", 
                    Path.GetFileName(filePath), headers.Count, string.Join(", ", ExposureKeys));
        }
        else
        {
            _logger.LogDebug("Found exposure in headers for {File}: {Exposure}s", Path.GetFileName(filePath), exposure);
        }

        string? binning = TryGetString(headers, BinningKeys);
        double? gain = TryGetDouble(headers, GainKeys);
        double? offset = TryGetDouble(headers, OffsetKeys);
        double? temp = TryGetDouble(headers, TempKeys);
        if (!temp.HasValue)
        {
            temp = InferTemperatureFromFilename(filePath);
            if (temp.HasValue)
                _logger.LogDebug("Inferred temperature from filename {File}: {Temp}°C", Path.GetFileName(filePath), temp);
        }
        string? filter = TryGetString(headers, FilterKeys);
        DateTime? obsDate = TryGetDateTime(headers, DateKeys);
        ImageType imageType = InferImageType(filePath, TryGetString(headers, ImageTypeKeys));

        return new ImageMetadata
        {
            FilePath = filePath,
            Type = imageType,
            ExposureTime = exposure,
            Binning = binning?.ToUpperInvariant(),
            Gain = gain,
            Offset = offset,
            Temperature = temp,
            Filter = filter,
            ObservationDate = obsDate
        };
    }

    private ImageMetadata CreateFallbackMetadata(string filePath)
    {
        return new ImageMetadata
        {
            FilePath = filePath,
            Type = InferImageType(filePath, null),
            ExposureTime = InferExposureFromFilename(filePath)
        };
    }

    private static string? TryGetString(Dictionary<string, string> headers, string[] keys)
    {
        foreach (var key in keys)
        {
            if (headers.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static double? TryGetDouble(Dictionary<string, string> headers, string[] keys)
    {
        var str = TryGetString(headers, keys);
        if (string.IsNullOrEmpty(str))
            return null;
        
        if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        
        return null;
    }

    private static DateTime? TryGetDateTime(Dictionary<string, string> headers, string[] keys)
    {
        var str = TryGetString(headers, keys);
        return DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : null;
    }

    private double? InferExposureFromFilename(string filePath)
    {
        var filename = Path.GetFileName(filePath);
        
        _logger.LogDebug("InferExposure: Testing filename [{Filename}]", filename);
        
        var match = ExposurePattern1().Match(filename);
        _logger.LogDebug("InferExposure: Pattern1 match: {Success}, Value: {Value}", 
            match.Success, match.Success ? match.Groups[1].Value : "none");
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var exp1))
        {
            _logger.LogDebug("InferExposure: {File} -> {Exposure}s from pattern1", filename, exp1);
            return exp1;
        }
        
        match = ExposurePattern2().Match(filename);
        _logger.LogDebug("InferExposure: Pattern2 match: {Success}, Value: {Value}", 
            match.Success, match.Success ? match.Groups[1].Value : "none");
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var exp2))
        {
            _logger.LogDebug("InferExposure: {File} -> {Exposure}s from pattern2", filename, exp2);
            return exp2;
        }
        
        _logger.LogDebug("InferExposure: {File} - No match found", filename);
        return null;
    }

    private static double? InferTemperatureFromFilename(string filePath)
    {
        var filename = Path.GetFileName(filePath);
        var match = TempPattern().Match(filename);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var temp))
            return temp;
        return null;
    }

    private static ImageType InferImageType(string filePath, string? headerType)
    {
        var fileName = Path.GetFileName(filePath).ToUpperInvariant();
        var typeStr = headerType?.ToUpperInvariant() ?? "";
        
        if (fileName.Contains("MASTERDARKFLAT") || typeStr.Contains("MASTER DARK FLAT"))
            return ImageType.MasterDarkFlat;
        if (fileName.Contains("MASTERDARK") || typeStr.Contains("MASTER DARK"))
            return ImageType.MasterDark;
        if (fileName.Contains("MASTERFLAT") || typeStr.Contains("MASTER FLAT"))
            return ImageType.MasterFlat;
        if (fileName.Contains("MASTERBIAS") || typeStr.Contains("MASTER BIAS"))
            return ImageType.MasterBias;
        if (fileName.Contains("DARKFLAT") || typeStr.Contains("DARK FLAT"))
            return ImageType.DarkFlat;
        if (fileName.Contains("DARK") || typeStr.Contains("DARK"))
            return ImageType.Dark;
        if (fileName.Contains("FLAT") || typeStr.Contains("FLAT"))
            return ImageType.Flat;
        if (fileName.Contains("BIAS") || typeStr.Contains("BIAS"))
            return ImageType.Bias;
        if (fileName.Contains("LIGHT") || typeStr.Contains("LIGHT"))
            return ImageType.Light;
        
        return ImageType.Unknown;
    }
}

