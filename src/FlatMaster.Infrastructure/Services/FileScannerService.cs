using System.Globalization;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Scans directories for flat and dark frames
/// </summary>
public sealed class FileScannerService : IFileScannerService
{
    private readonly IMetadataReaderService _metadataReader;
    private readonly ILogger<FileScannerService> _logger;
    
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_darkmasters", "_calibratedflats", "masters", "_processed"
    };
    
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fits", ".fit", ".xisf"
    };

    public FileScannerService(IMetadataReaderService metadataReader, ILogger<FileScannerService> logger)
    {
        _metadataReader = metadataReader;
        _logger = logger;
    }

    public async Task<List<DirectoryJob>> ScanFlatDirectoriesAsync(
        IEnumerable<string> baseRoots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var jobs = new List<DirectoryJob>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dirCount = 0;
        int fileCount = 0;
        int fitsCount = 0;
        int xisfCount = 0;

        foreach (var baseRoot in baseRoots)
        {
            if (!Directory.Exists(baseRoot))
            {
                _logger.LogWarning("Base root not found: {BaseRoot}", baseRoot);
                continue;
            }

            var outputRoot = GetProcessedSiblingPath(baseRoot);

            foreach (var directory in EnumerateDirectories(baseRoot))
            {
                dirCount++;

                if (seenDirectories.Contains(directory))
                    continue;

                var imageFiles = await GetImageFilesAsync(directory, cancellationToken);
                
                if (imageFiles.Count == 0)
                    continue;

                // Filter out existing master flats
                var beforeFilter = imageFiles.Count;
                imageFiles = imageFiles
                    .Where(f => !IsMasterFlat(Path.GetFileName(f)))
                    .ToList();

                if (beforeFilter != imageFiles.Count)
                    _logger.LogInformation("{Directory}: Filtered out {Count} master flats", directory, beforeFilter - imageFiles.Count);

                if (imageFiles.Count == 0)
                    continue;

                fileCount += imageFiles.Count;
                fitsCount += imageFiles.Count(f => HasExtension(f, ".fit") || HasExtension(f, ".fits"));
                xisfCount += imageFiles.Count(f => HasExtension(f, ".xisf"));
                seenDirectories.Add(directory);

                progress?.Report(new ScanProgress
                {
                    DirectoriesScanned = dirCount,
                    FilesFound = fileCount,
                    FitsFound = fitsCount,
                    XisfFound = xisfCount,
                    CurrentDirectory = directory
                });

                var metadata = await _metadataReader.ReadMetadataBatchAsync(imageFiles, cancellationToken);
                
                // Log sample of image types detected
                var typesSample = metadata.Take(3).Select(kvp => $"{Path.GetFileName(kvp.Key)}: Type={kvp.Value.Type}, Exp={kvp.Value.ExposureTime}").ToList();
                _logger.LogInformation("{Directory}: {FileCount} files, {MetaCount} with metadata. Sample: {Sample}", 
                    directory, imageFiles.Count, metadata.Count, string.Join(" | ", typesSample));
                
                var exposureGroups = GroupByExposure(imageFiles, metadata);

                // Filter groups with < 3 files
                var validGroups = exposureGroups.Where(g => g.IsValid).ToList();
                var invalidCount = exposureGroups.Count - validGroups.Count;
                
                if (exposureGroups.Count > 0)
                {
                    _logger.LogInformation("{Directory}: {Total} exposure groups, {Valid} valid (>=3 files), {Invalid} skipped (<3 files)",
                        directory, exposureGroups.Count, validGroups.Count, invalidCount);
                }
                
                if (validGroups.Count == 0)
                {
                    if (exposureGroups.Count > 0)
                        _logger.LogWarning("{Directory}: Skipped - all {Count} groups had <3 files", directory, exposureGroups.Count);
                    else
                        _logger.LogWarning("{Directory}: Skipped - no exposure groups (missing exposure time or wrong ImageType?)", directory);
                    continue;
                }

                var relativeDir = Path.GetRelativePath(baseRoot, directory);
                if (relativeDir == ".")
                    relativeDir = "";

                jobs.Add(new DirectoryJob
                {
                    DirectoryPath = directory,
                    BaseRootPath = baseRoot,
                    OutputRootPath = outputRoot,
                    RelativeDirectory = relativeDir,
                    ExposureGroups = validGroups
                });
            }
        }

        progress?.Report(new ScanProgress
        {
            DirectoriesScanned = dirCount,
            FilesFound = fileCount,
            FitsFound = fitsCount,
            XisfFound = xisfCount,
            CurrentDirectory = string.Empty
        });

        _logger.LogInformation("Flat scan complete: {DirCount} directories, {FileCount} files scanned (FITS={Fits}, XISF={Xisf}), {TotalFlats} flats found in {JobCount} valid groups",
            dirCount, fileCount, fitsCount, xisfCount, jobs.Sum(j => j.ExposureGroups.Sum(g => g.FilePaths.Count)), jobs.Count);

        return jobs;
    }

    public async Task<List<DarkFrame>> ScanDarkLibraryAsync(
        IEnumerable<string> darkRoots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var darkFrames = new List<DarkFrame>();
        int dirCount = 0;
        int fileCount = 0;
        int fitsCount = 0;
        int xisfCount = 0;

        foreach (var darkRoot in darkRoots)
        {
            if (!Directory.Exists(darkRoot))
            {
                _logger.LogWarning("Dark root not found: {DarkRoot}", darkRoot);
                continue;
            }

            foreach (var directory in EnumerateDirectories(darkRoot))
            {
                dirCount++;

                var imageFiles = await GetImageFilesAsync(directory, cancellationToken);
                _logger.LogDebug("{Directory}: Found {Count} image files for dark scan", directory, imageFiles.Count);
                fileCount += imageFiles.Count;
                fitsCount += imageFiles.Count(f => HasExtension(f, ".fit") || HasExtension(f, ".fits"));
                xisfCount += imageFiles.Count(f => HasExtension(f, ".xisf"));

                progress?.Report(new ScanProgress
                {
                    DirectoriesScanned = dirCount,
                    FilesFound = fileCount,
                    FitsFound = fitsCount,
                    XisfFound = xisfCount,
                    CurrentDirectory = directory
                });

                var metadata = await _metadataReader.ReadMetadataBatchAsync(imageFiles, cancellationToken);
                _logger.LogDebug("{Directory}: Read metadata for {Count}/{Total} dark files", directory, metadata.Count, imageFiles.Count);
                
                var beforeDarkFilter = metadata.Count;
                var darkTypesFound = new Dictionary<ImageType, int>();
                
                foreach (var (path, meta) in metadata)
                {
                    if (!darkTypesFound.ContainsKey(meta.Type))
                        darkTypesFound[meta.Type] = 0;
                    darkTypesFound[meta.Type]++;
                    
                    if (!IsDarkType(meta.Type) || !meta.ExposureTime.HasValue)
                        continue;

                    darkFrames.Add(new DarkFrame
                    {
                        FilePath = path,
                        Type = meta.Type,
                        ExposureTime = meta.ExposureTime.Value,
                        Binning = meta.Binning,
                        Gain = meta.Gain,
                        Offset = meta.Offset,
                        Temperature = meta.Temperature
                    });
                }
                
                var addedFromDir = metadata.Count(kvp => IsDarkType(kvp.Value.Type) && kvp.Value.ExposureTime.HasValue);
                _logger.LogDebug("{Directory}: Added {Count} darks. Image types found: {Types}", 
                    directory, addedFromDir, string.Join(", ", darkTypesFound.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }
        }

        progress?.Report(new ScanProgress
        {
            DirectoriesScanned = dirCount,
            FilesFound = fileCount,
            FitsFound = fitsCount,
            XisfFound = xisfCount,
            CurrentDirectory = string.Empty
        });

        _logger.LogInformation("Dark scan complete: {DirCount} directories, {FileCount} files scanned (FITS={Fits}, XISF={Xisf}), {DarkCount} darks found",
            dirCount, fileCount, fitsCount, xisfCount, darkFrames.Count);

        // Backfill missing temperatures: master darks from WBPP often have temperature stripped.
        // Infer from sub-frame darks that share the same binning.
        BackfillMissingTemperatures(darkFrames);

        return darkFrames;
    }

    public async Task<List<string>> GetImageFilesAsync(string directory, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var allFiles = Directory.EnumerateFiles(directory).ToList();
                var imageFiles = allFiles.Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                
                if (allFiles.Count > 0 && imageFiles.Count == 0)
                {
                    var extensions = allFiles.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().Take(10).ToList();
                    _logger.LogDebug("{Directory}: {Total} files but 0 match supported extensions. Found: {Extensions}", 
                        directory, allFiles.Count, string.Join(", ", extensions));
                }
                
                return imageFiles;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied: {Directory}", directory);
                return new List<string>();
            }
        }, cancellationToken);
    }

    private IEnumerable<string> EnumerateDirectories(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            try
            {
                foreach (var subdir in Directory.EnumerateDirectories(current))
                {
                    var dirName = Path.GetFileName(subdir);
                    if (!ShouldSkipDirectory(dirName))
                    {
                        queue.Enqueue(subdir);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied: {Directory}", current);
            }
        }
    }

    private List<ExposureGroup> GroupByExposure(List<string> filePaths, Dictionary<string, ImageMetadata> metadata)
    {
        var groups = new Dictionary<string, List<string>>();
        var representativeMetadata = new Dictionary<string, ImageMetadata>();

        foreach (var path in filePaths)
        {
            if (!metadata.TryGetValue(path, out var meta) || !meta.ExposureTime.HasValue)
                continue;

            var key = meta.ExposureKey;
            if (!groups.ContainsKey(key))
            {
                groups[key] = new List<string>();
                representativeMetadata[key] = meta;
            }
            groups[key].Add(path);
        }

        return groups
            .OrderBy(kvp => double.Parse(kvp.Key.TrimEnd('s'), CultureInfo.InvariantCulture))
            .Select(kvp =>
            {
                var meta = representativeMetadata[kvp.Key];
                return new ExposureGroup
                {
                    ExposureTime = meta.ExposureTime!.Value,
                    FilePaths = kvp.Value,
                    RepresentativeMetadata = meta,
                    MatchingCriteria = new MatchingCriteria
                    {
                        Binning = meta.Binning,
                        Gain = meta.Gain,
                        Offset = meta.Offset,
                        Temperature = meta.Temperature
                    }
                };
            })
            .ToList();
    }

    private static bool ShouldSkipDirectory(string dirName) 
        => dirName.StartsWith('.') || SkipDirectories.Contains(dirName);

    private static bool IsMasterFlat(string fileName) 
        => fileName.StartsWith("MasterFlat_", StringComparison.OrdinalIgnoreCase);

    private static bool HasExtension(string filePath, string extension)
        => string.Equals(Path.GetExtension(filePath), extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsDarkType(ImageType type) 
        => type is ImageType.Dark or ImageType.DarkFlat or ImageType.MasterDark or ImageType.MasterDarkFlat;

    /// <summary>
    /// Master darks from WBPP/ImageIntegration often lack CCD-TEMP headers.
    /// Backfill from sub-frame darks that have temperature and share the same binning.
    /// Uses median temperature of available sub-frames as the inferred value.
    /// </summary>
    private void BackfillMissingTemperatures(List<DarkFrame> darkFrames)
    {
        var needTemp = darkFrames.Where(d => !d.Temperature.HasValue).ToList();
        if (needTemp.Count == 0) return;

        var haveTemp = darkFrames.Where(d => d.Temperature.HasValue).ToList();
        if (haveTemp.Count == 0)
        {
            _logger.LogDebug("No darks with temperature available for backfill ({Count} darks missing temp)", needTemp.Count);
            return;
        }

        int filled = 0;
        foreach (var dark in needTemp)
        {
            // Find sub-frame darks with same binning that have temperature
            var donors = haveTemp
                .Where(d => string.Equals(d.Binning, dark.Binning, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Temperature!.Value)
                .OrderBy(t => t)
                .ToList();

            if (donors.Count > 0)
            {
                // Use median temperature
                var median = donors[donors.Count / 2];
                dark.Temperature = median;
                filled++;
            }
        }

        if (filled > 0)
            _logger.LogInformation("Backfilled temperature for {Filled}/{Total} darks (inferred from sub-frame siblings)",
                filled, needTemp.Count);
    }

    private static string GetProcessedSiblingPath(string basePath)
    {
        var dirInfo = new DirectoryInfo(basePath);
        var parentDir = dirInfo.Parent?.FullName ?? Path.GetDirectoryName(basePath);
        var processedName = dirInfo.Name + "_processed";
        return Path.Combine(parentDir ?? "", processedName);
    }
}
