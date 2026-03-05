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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;
/// <summary>
/// Scans directories for flat and dark frames
/// </summary>
public sealed partial class FileScannerService(IMetadataReaderService metadataReader, ILogger<FileScannerService> logger) : IFileScannerService
{
    // Regex for matching DARKSXXX or DARKXXX (2-4 digits)
    private static readonly Regex DarksFolderRegex = MyRegex();

    /// <summary>
    /// Finds the nearest ancestor directory matching DARKSXXX or DARKXXX (2-4 digits)
    /// </summary>
    public static string? FindDarksAncestor(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (name != null && DarksFolderRegex.IsMatch(name))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
    private readonly IMetadataReaderService _metadataReader = metadataReader;
    private readonly ILogger<FileScannerService> _logger = logger;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_darkmasters", "_calibratedflats", "masters", "_processed"
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fits", ".fit", ".xisf"
    };

    // Match FlatMaster-generated outputs: MasterFlat_<date>_<filter>_<exp>s.(xisf|fit|fits)
    private static readonly Regex GeneratedMasterFlatRegex = MyRegex1();

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
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(baseRoot))
            {
                _logger.LogWarning("Base root not found: {BaseRoot}", baseRoot);
                continue;
            }

            var outputRoot = GetProcessedSiblingPath(baseRoot);

            foreach (var directory in EnumerateDirectories(baseRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dirCount++;

                if (seenDirectories.Contains(directory))
                    continue;

                var imageFiles = await GetImageFilesAsync(directory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await _metadataReader.ReadMetadataBatchAsync(imageFiles, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug("{Directory}: Read metadata for {Count}/{Total} flat files", directory, metadata.Count, imageFiles.Count);

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

                var groups = GroupByExposure(imageFiles, metadata);
                if (groups.Count == 0)
                {
                    seenDirectories.Add(directory);
                    continue;
                }

                var relative = Path.GetRelativePath(baseRoot, directory);
                var job = new DirectoryJob
                {
                    DirectoryPath = directory,
                    BaseRootPath = baseRoot,
                    OutputRootPath = outputRoot,
                    RelativeDirectory = relative,
                    ExposureGroups = groups
                };

                jobs.Add(job);
                seenDirectories.Add(directory);
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

        return jobs;
    }

    public async Task<List<DarkFrame>> ScanDarkLibraryAsync(
        IEnumerable<string> darkRoots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var darkFrames = new List<DarkFrame>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dirCount = 0;
        int fileCount = 0;
        int fitsCount = 0;
        int xisfCount = 0;

        foreach (var darkRoot in darkRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(darkRoot))
            {
                _logger.LogWarning("Dark root not found: {DarkRoot}", darkRoot);
                continue;
            }

            // Pre-scan for master darks and manifests which may live under skipped directories
            try
            {
                // 1) Find master files by naming convention
                cancellationToken.ThrowIfCancellationRequested();
                var masterCandidates = new List<string>();
                foreach (var ext in new[] { ".xisf", ".fit", ".fits" })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(darkRoot, "*MasterDark*" + ext, SearchOption.AllDirectories))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            masterCandidates.Add(path);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }

                if (masterCandidates.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var meta = await _metadataReader.ReadMetadataBatchAsync([.. masterCandidates.Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);
                    foreach (var kvp in meta)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = kvp.Key;
                        var m = kvp.Value;
                        if (addedPaths.Contains(path)) continue;

                        var allowMissingExposure = m.Type is ImageType.Bias or ImageType.MasterBias;

                        if (!m.ExposureTime.HasValue && (m.Type is ImageType.MasterDark or ImageType.MasterDarkFlat))
                        {
                            try
                            {
                                var manifestPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "MasterDark.meta.json");
                                if (File.Exists(manifestPath))
                                {
                                    var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                                    var manifest = System.Text.Json.JsonSerializer.Deserialize<FlatMaster.Core.Models.MasterDarkManifest>(json);
                                    if (manifest != null)
                                    {
                                        var appliedFields = new List<string>();
                                        if (!m.ExposureTime.HasValue) appliedFields.Add("Exposure");
                                        if (!m.Temperature.HasValue && manifest.TemperatureMedianC.HasValue) appliedFields.Add("Temperature");
                                        if (string.IsNullOrWhiteSpace(m.Binning) && !string.IsNullOrWhiteSpace(manifest.Binning)) appliedFields.Add("Binning");
                                        if (!m.Gain.HasValue && manifest.Gain.HasValue) appliedFields.Add("Gain");

                                        m = m with
                                        {
                                            ExposureTime = m.ExposureTime ?? manifest.ExposureSeconds,
                                            Temperature = m.Temperature ?? manifest.TemperatureMedianC,
                                            Binning = string.IsNullOrWhiteSpace(m.Binning) ? manifest.Binning : m.Binning,
                                            Gain = m.Gain ?? manifest.Gain,
                                            Type = ImageType.MasterDark
                                        };

                                        if (appliedFields.Count > 0)
                                            _logger.LogInformation("Applied MasterDark.meta.json for {Path}: set {Fields}", path, string.Join(", ", appliedFields));
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!IsDarkType(m.Type) || (!m.ExposureTime.HasValue && !allowMissingExposure))
                            continue;

                        darkFrames.Add(new DarkFrame
                        {
                            FilePath = path,
                            Type = m.Type,
                            ExposureTime = m.ExposureTime ?? 0.0,
                            Binning = m.Binning,
                            Gain = m.Gain,
                            Offset = m.Offset,
                            Temperature = m.Temperature,
                            DarkGroupFolder = FindDarksAncestor(path)
                        });
                        addedPaths.Add(path);
                    }
                }

                // 2) Find manifest files and apply to files in the same directory
                cancellationToken.ThrowIfCancellationRequested();
                var manifestFiles = new List<string>();
                try
                {
                    foreach (var path in Directory.EnumerateFiles(darkRoot, "MasterDark.meta.json", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        manifestFiles.Add(path);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                foreach (var manifestPath in manifestFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                        var manifest = System.Text.Json.JsonSerializer.Deserialize<FlatMaster.Core.Models.MasterDarkManifest>(json);
                        if (manifest == null) continue;

                        var dir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
                        var candidates = Directory.EnumerateFiles(dir)
                            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                            .ToList();

                        if (candidates.Count == 0)
                            continue;

                        var meta = await _metadataReader.ReadMetadataBatchAsync(candidates, cancellationToken);
                        foreach (var kvp in meta)
                        {
                            var path = kvp.Key;
                            if (addedPaths.Contains(path)) continue;
                            var m = kvp.Value;

                            var applied = false;
                            var allowMissingExposure = m.Type is ImageType.Bias or ImageType.MasterBias;

                            if (!m.ExposureTime.HasValue)
                            {
                                m = m with { ExposureTime = manifest.ExposureSeconds };
                                applied = true;
                            }
                            if (!m.Temperature.HasValue && manifest.TemperatureMedianC.HasValue)
                            {
                                m = m with { Temperature = manifest.TemperatureMedianC };
                                applied = true;
                            }
                            if (string.IsNullOrWhiteSpace(m.Binning) && !string.IsNullOrWhiteSpace(manifest.Binning))
                            {
                                m = m with { Binning = manifest.Binning };
                                applied = true;
                            }
                            if (!m.Gain.HasValue && manifest.Gain.HasValue)
                            {
                                m = m with { Gain = manifest.Gain };
                                applied = true;
                            }

                            if (applied)
                            {
                                m = m with { Type = ImageType.MasterDark };
                                _logger.LogInformation("Manifest {Manifest} applied to {File} (fields: Exposure/Temp/Binning/Gain)", manifestPath, path);
                            }

                            if (!IsDarkType(m.Type) || (!m.ExposureTime.HasValue && !allowMissingExposure))
                                continue;

                            darkFrames.Add(new DarkFrame
                            {
                                FilePath = path,
                                Type = m.Type,
                                ExposureTime = m.ExposureTime ?? 0.0,
                                Binning = m.Binning,
                                Gain = m.Gain,
                                Offset = m.Offset,
                                Temperature = m.Temperature,
                                DarkGroupFolder = FindDarksAncestor(path)
                            });
                            addedPaths.Add(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed processing manifest {Manifest}: {Msg}", manifestPath, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pre-scan for master darks under '{Root}' failed: {Msg}", darkRoot, ex.Message);
            }

            // Regular directory scan
            foreach (var directory in EnumerateDirectories(darkRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                dirCount++;

                var imageFiles = await GetImageFilesAsync(directory, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug("{Directory}: Read metadata for {Count}/{Total} dark files", directory, metadata.Count, imageFiles.Count);

                var darkTypesFound = new Dictionary<ImageType, int>();

                foreach (var kvp in metadata)
                {
                    var path = kvp.Key;
                    var meta = kvp.Value;

                    if (!darkTypesFound.ContainsKey(meta.Type))
                        darkTypesFound[meta.Type] = 0;
                    darkTypesFound[meta.Type]++;

                    var allowMissingExposure = meta.Type is ImageType.Bias or ImageType.MasterBias;
                    if (!IsDarkType(meta.Type) || (!meta.ExposureTime.HasValue && !allowMissingExposure))
                        continue;

                    if (addedPaths.Contains(path))
                        continue;

                    darkFrames.Add(new DarkFrame
                    {
                        FilePath = path,
                        Type = meta.Type,
                        ExposureTime = meta.ExposureTime ?? 0.0,
                        Binning = meta.Binning,
                        Gain = meta.Gain,
                        Offset = meta.Offset,
                        Temperature = meta.Temperature,
                        DarkGroupFolder = FindDarksAncestor(path)
                    });
                    addedPaths.Add(path);
                }

                var addedFromDir = metadata.Count(kvp =>
                    IsDarkType(kvp.Value.Type) &&
                    (kvp.Value.ExposureTime.HasValue || kvp.Value.Type is ImageType.Bias or ImageType.MasterBias));
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
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var imageFiles = new List<string>();
                var extensionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var totalFiles = 0;

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalFiles++;
                    var ext = Path.GetExtension(file);
                    if (!string.IsNullOrEmpty(ext))
                        extensionSet.Add(ext);

                    if (SupportedExtensions.Contains(ext))
                        imageFiles.Add(file);
                }

                imageFiles.Sort(StringComparer.OrdinalIgnoreCase);

                if (totalFiles > 0 && imageFiles.Count == 0)
                {
                    var extensions = extensionSet.Select(e => e.ToLowerInvariant()).Take(10).ToList();
                    _logger.LogDebug("{Directory}: {Total} files but 0 match supported extensions. Found: {Extensions}",
                        directory, totalFiles, string.Join(", ", extensions));
                }

                return imageFiles;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied: {Directory}", directory);
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found: {Directory}", directory);
                return [];
            }
        }, cancellationToken);
    }

    private IEnumerable<string> EnumerateDirectories(string root, CancellationToken cancellationToken = default)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            yield return current;

            try
            {
                foreach (var subdir in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dirName = Path.GetFileName(subdir);
                    if (!ShouldSkipDirectory(dirName))
                    {
                        queue.Enqueue(subdir);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Access denied: {Directory}", current);
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found while enumerating: {Directory}", current);
            }
        }
    }

    private static List<ExposureGroup> GroupByExposure(List<string> filePaths, Dictionary<string, ImageMetadata> metadata)
    {
        var groups = new Dictionary<string, List<string>>();
        var representativeMetadata = new Dictionary<string, ImageMetadata>();

        foreach (var path in filePaths)
        {
            if (!metadata.TryGetValue(path, out var meta) || !meta.ExposureTime.HasValue)
                continue;
            if (IsDarkType(meta.Type))
                continue;

            var key = meta.ExposureKey;
            if (!groups.TryGetValue(key, out List<string>? value))
            {
                value = [];
                groups[key] = value;
                representativeMetadata[key] = meta;
            }

            value.Add(path);
        }

        return [.. groups
            .OrderBy(kvp => double.Parse(kvp.Key.TrimEnd('s'), CultureInfo.InvariantCulture))
            .Select(kvp =>
            {
                var meta = representativeMetadata[kvp.Key];
                var temperatureValues = kvp.Value
                    .Select(path => metadata.TryGetValue(path, out var m) ? m.Temperature : null)
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToList();
                var avgTemperatureOrNull = temperatureValues.Count > 0
                    ? temperatureValues.Average()
                    : (double?)null;

                return new ExposureGroup
                {
                    ExposureTime = meta.ExposureTime!.Value,
                    FilePaths = kvp.Value,
                    RepresentativeMetadata = meta,
                    AverageTemperatureC = avgTemperatureOrNull,
                    MatchingCriteria = new MatchingCriteria
                    {
                        Binning = meta.Binning,
                        Gain = meta.Gain,
                        Offset = meta.Offset,
                        Temperature = avgTemperatureOrNull ?? meta.Temperature
                    }
                };
            })];
    }

    private static bool ShouldSkipDirectory(string dirName)
        => dirName.StartsWith('.') || SkipDirectories.Contains(dirName);

    private static bool IsGeneratedMasterFlat(string fileName)
        => GeneratedMasterFlatRegex.IsMatch(fileName);

    private static bool HasExtension(string filePath, string extension)
        => string.Equals(Path.GetExtension(filePath), extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsDarkType(ImageType type)
        => type is ImageType.Dark or ImageType.DarkFlat or ImageType.MasterDark or ImageType.MasterDarkFlat
            or ImageType.Bias or ImageType.MasterBias;

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

    [GeneratedRegex(@"^DARKS?\d{2,4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nb-NO")]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"^MasterFlat_.+_[0-9]+(?:\.[0-9]+)?s\.(?:xisf|fit|fits)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nb-NO")]
    private static partial Regex MyRegex1();
}
