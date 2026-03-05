using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Materializes master darks from raw dark folders when needed.
/// Minimal, deterministic implementation that matches native integration behavior.
/// </summary>
public sealed class MasterDarkMaterializer(
    ILogger<MasterDarkMaterializer> logger,
    IMetadataReaderService metadataReader,
    IDarkMatchingService darkMatcher,
    IPixInsightService pixInsight,
    IImageProcessingEngine nativeEngine) : IMasterDarkMaterializer
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private readonly ILogger<MasterDarkMaterializer> _logger = logger;
    private const int CurrentMasterDarkPipelineVersion = 3;
    private readonly IMetadataReaderService _metadataReader = metadataReader;
    private readonly IDarkMatchingService _darkMatcher = darkMatcher;
    private readonly IPixInsightService _pixInsight = pixInsight;
    private readonly IImageProcessingEngine _nativeEngine = nativeEngine;

    private sealed class PixInsightMaterializeRequest
    {
        public required string SourceLabel { get; init; }
        public required string OutputRoot { get; init; }
        public required string OutputDirectory { get; init; }
        public required List<string> FilePaths { get; init; }
        public required double ExposureSeconds { get; init; }
        public required string Binning { get; init; }
        public required double? Gain { get; init; }
        public required double? TemperatureC { get; init; }
        public ImageMetadata? RepresentativeMetadata { get; init; }
        public required string PixInsightExecutable { get; init; }
        public required string ErrorContext { get; init; }
    }

    public async Task<List<string>> MaterializeMastersAsync(
        ProcessingPlan plan,
        IEnumerable<string> darkRoots,
        bool preferNative,
        string? pixInsightExecutable,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        void Report(string message)
        {
            _logger.LogInformation("{Message}", message);
            progress?.Report(message);
        }

        var createdBag = new System.Collections.Concurrent.ConcurrentBag<string>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var planLock = new object();

        // Detect required raw-dark folders using shared logic
        var requiredFolders = DetectRequiredFolders(plan, darkRoots);

        if (requiredFolders.Count == 0)
        {
            _logger.LogDebug("MasterDarkMaterializer: No raw-dark folders require materialization.");
            Report("[MasterDark] No raw dark folders require materialization.");
            // Import existing masters referenced in the catalog under the output root
            try
            {
                var outputRoot = plan.Jobs.FirstOrDefault()?.OutputRootPath;
                if (!string.IsNullOrWhiteSpace(outputRoot))
                {
                    var masters = plan.DarkCatalog
                        .Where(d => d.Type == ImageType.MasterDark || d.Type == ImageType.MasterDarkFlat)
                        .Select(d => d.FilePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var mpath in masters)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(mpath) || !File.Exists(mpath))
                                continue;

                            var fullOutRoot = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            var fullMaster = Path.GetFullPath(mpath);
                            if (fullMaster.StartsWith(fullOutRoot, StringComparison.OrdinalIgnoreCase))
                                continue;

                            string? keyHash = null;
                            var sourceEntry = plan.DarkCatalog.FirstOrDefault(d =>
                                string.Equals(Path.GetFullPath(d.FilePath), fullMaster, StringComparison.OrdinalIgnoreCase));
                            var manifestPath = Path.Combine(Path.GetDirectoryName(mpath) ?? string.Empty, "MasterDark.meta.json");
                            double? manifestTemp = null;
                            if (File.Exists(manifestPath))
                            {
                                try
                                {
                                    var manifestJson = File.ReadAllText(manifestPath);
                                    var manifest = JsonSerializer.Deserialize<MasterDarkManifest>(manifestJson);
                                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Key))
                                        keyHash = manifest.Key;
                                    manifestTemp = manifest?.TemperatureMedianC;
                                }
                                catch { }
                            }

                            double? exposureFromHeader = null;
                            string? binningFromHeader = null;
                            double? gainFromHeader = null;
                            if (string.IsNullOrWhiteSpace(keyHash))
                            {
                                try
                                {
                                    var fits = new FitsImageIO(_logger);
                                    var info = fits.ReadAsync(mpath, CancellationToken.None).GetAwaiter().GetResult();
                                    if (info.Headers.TryGetValue("EXPOSURE", out var eVal) && double.TryParse(eVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var ev))
                                        exposureFromHeader = ev;
                                    if (info.Headers.TryGetValue("BINNING", out var bVal))
                                        binningFromHeader = bVal;
                                    if (info.Headers.TryGetValue("GAIN", out var gVal) && double.TryParse(gVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var gv))
                                        gainFromHeader = gv;
                                    keyHash = FlatMaster.Core.Utilities.MasterDarkUtilities.ComputeMasterKeyHash(
                                        exposureFromHeader ?? sourceEntry?.ExposureTime ?? 0.0,
                                        binningFromHeader ?? sourceEntry?.Binning ?? "1",
                                        gainFromHeader ?? sourceEntry?.Gain,
                                        info.Width,
                                        info.Height);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to compute key for existing master {Path}", mpath);
                                }
                            }

                            if (string.IsNullOrWhiteSpace(keyHash))
                                continue;

                            var exposureForPath = sourceEntry?.ExposureTime ?? exposureFromHeader ?? 0.0;
                            var tempForPath = sourceEntry?.Temperature ?? manifestTemp;
                            var outDir = MasterDarkPathing.BuildMasterDarkOutputDirectory(outputRoot, exposureForPath, tempForPath);
                            Directory.CreateDirectory(outDir);
                            var destMaster = Path.Combine(
                                outDir,
                                MasterDarkPathing.BuildMasterDarkFileName(exposureForPath, tempForPath, plan.Configuration.OutputFileExtension));
                            var destMeta = Path.Combine(outDir, "MasterDark.meta.json");
                            try { File.Copy(mpath, destMaster, overwrite: true); } catch (Exception ex) { _logger.LogWarning(ex, "Failed copying master {Src} -> {Dst}", mpath, destMaster); }
                            if (File.Exists(manifestPath))
                            {
                                try { File.Copy(manifestPath, destMeta, overwrite: true); } catch { }
                            }

                            _logger.LogInformation("Imported existing master dark into output root: {Path}", destMaster);
                            Report($"[MasterDark] Imported existing master: {destMaster}");
                            RegisterMasterInPlan(createdBag, planLock, plan, destMaster, exposureForPath, sourceEntry?.Binning ?? binningFromHeader, sourceEntry?.Gain ?? gainFromHeader, tempForPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed importing existing master {Path}", mpath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-scan import of existing masters failed: {Msg}", ex.Message);
            }

            return [.. createdBag];
        }

        _logger.LogInformation("MasterDarkMaterializer: Will build {Count} master dark(s) from raw dark folders under output root.", requiredFolders.Count);
        Report($"[MasterDark] Building {requiredFolders.Count} required master dark folder(s).");

        // Local function to process a single folder (keeps code DRY for parallel/sequential paths)
        async Task ProcessFolderAsync(KeyValuePair<string, List<(DirectoryJob job, ExposureGroup group)>> kvp, CancellationToken ct)
        {
            var folder = kvp.Key;
            try
            {
                ct.ThrowIfCancellationRequested();
                Report($"[MasterDark] Scanning source folder: {folder}");

                var candidateFiles = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                        f.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidateFiles.Count == 0)
                {
                    throw new InvalidOperationException($"Master dark materialization failed: no image frames found in {folder}");
                }

                var metaBatch = await _metadataReader.ReadMetadataBatchAsync(candidateFiles, ct);
                var files = candidateFiles
                    .Where(f => IsRawDarkInputFile(f, metaBatch, plan.DarkCatalog))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _logger.LogInformation(
                    "MaterializeMastersAsync: Integrating {RawCount}/{TotalCount} raw-dark files from folder {Folder} into master dark.",
                    files.Count,
                    candidateFiles.Count,
                    folder);
                Report($"[MasterDark] {Path.GetFileName(folder)}: using {files.Count}/{candidateFiles.Count} raw dark frames.");

                if (files.Count == 0)
                    throw new InvalidOperationException($"Master dark materialization failed: no raw dark frames found in {folder}");

                double? exposureFromName = FlatMaster.Core.Utilities.MasterDarkUtilities.ExtractExposureFromFolderName(Path.GetFileName(folder));
                double exposure = 0.0;
                var exposures = files
                    .Select(f => metaBatch.TryGetValue(f, out var m) ? m.ExposureTime : null)
                    .Where(e => e.HasValue)
                    .Select(e => e!.Value)
                    .ToList();
                if (exposureFromName.HasValue)
                {
                    exposure = exposureFromName.Value;
                    if (exposures.Count > 0)
                    {
                        var median = exposures.OrderBy(e => e).ElementAt(exposures.Count / 2);
                        if (Math.Abs(median - exposure) > 0.5)
                        {
                            throw new InvalidOperationException($"Exposure parse mismatch for folder {folder}: folder indicates {exposure:F3}s but median metadata is {median:F3}s");
                        }
                    }
                }
                else
                {
                    if (exposures.Count == 0)
                        throw new InvalidOperationException($"Cannot determine exposure for dark folder '{folder}': no parseable folder exposure and no usable metadata.");

                    exposure = exposures.OrderBy(e => e).ElementAt(exposures.Count / 2);
                }

                var sampleMeta = files
                    .Select(f => metaBatch.TryGetValue(f, out var m) ? m : null)
                    .FirstOrDefault(m => m != null);
                var binning = sampleMeta?.Binning ?? "1";
                var gain = sampleMeta?.Gain;
                var temperatureValues = files
                    .Select(f => metaBatch.TryGetValue(f, out var m) ? m.Temperature : null)
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .OrderBy(t => t)
                    .ToList();
                var normalizedTempMedian = temperatureValues.Count > 0
                    ? temperatureValues[temperatureValues.Count / 2]
                    : null as double?;
                int width = 0, height = 0;
                try
                {
                    var fits = new FitsImageIO(_logger);
                    var info = await fits.ReadAsync(files[0], ct);
                    width = info.Width; height = info.Height;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed reading resolution from {File}", files[0]);
                }

                var keyHash = FlatMaster.Core.Utilities.MasterDarkUtilities.ComputeMasterKeyHash(exposure, binning, gain, width, height);
                var outDir = MasterDarkPathing.BuildMasterDarkOutputDirectory(plan.Jobs.First().OutputRootPath, exposure, normalizedTempMedian);
                var masterPath = Path.Combine(
                    outDir,
                    MasterDarkPathing.BuildMasterDarkFileName(exposure, normalizedTempMedian, plan.Configuration.OutputFileExtension));
                var metaPath = Path.Combine(outDir, "MasterDark.meta.json");

                _logger.LogInformation("Materialize: folder={Folder} key={Key} outDir={OutDir} masterPath={MasterPath}", folder, keyHash, outDir, masterPath);

                if (await CanReuseExistingMasterAsync(masterPath, metaPath, keyHash, ct))
                {
                    _logger.LogInformation("Reusing existing master dark: {Path} (outDir={OutDir})", masterPath, outDir);
                    Report($"[MasterDark] Reusing existing master: {masterPath}");
                    RegisterMasterInPlan(createdBag, planLock, plan, masterPath, exposure, binning, gain, normalizedTempMedian);
                    return;
                }

                // Deduplicate by locking target directory
                Directory.CreateDirectory(Path.GetDirectoryName(outDir) ?? outDir);
                var tmpDir = outDir + ".tmp_" + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(tmpDir);

                try
                {
                    if (!preferNative)
                    {
                        if (string.IsNullOrWhiteSpace(pixInsightExecutable) || !File.Exists(pixInsightExecutable))
                            throw new InvalidOperationException("PixInsight mode selected but PixInsight executable not found.");

                        var outputRoot = plan.Jobs.First().OutputRootPath;
                        if (string.IsNullOrWhiteSpace(outputRoot))
                            throw new InvalidOperationException("Output root is required for PixInsight dark materialization.");
                        var request = new PixInsightMaterializeRequest
                        {
                            SourceLabel = folder,
                            OutputRoot = outputRoot,
                            OutputDirectory = outDir,
                            FilePaths = files,
                            ExposureSeconds = exposure,
                            Binning = binning,
                            Gain = gain,
                            TemperatureC = normalizedTempMedian,
                            RepresentativeMetadata = sampleMeta,
                            PixInsightExecutable = pixInsightExecutable!,
                            ErrorContext = folder
                        };

                        Report($"[MasterDark] {Path.GetFileName(folder)}: invoking PixInsight integration ({files.Count} frames).");
                        await MaterializeWithPixInsightAsync(plan, request, masterPath, cancellationToken, progress);
                        RegisterMasterInPlan(createdBag, planLock, plan, masterPath, exposure, binning, gain, normalizedTempMedian);
                        Report($"[MasterDark] Created master: {masterPath}");
                        return;
                    }

                    // Native integration path: load and integrate raw dark frames directly.
                    var stageLabel = $"Native master-dark ({Path.GetFileName(folder)})";
                    Report($"[MasterDark] {Path.GetFileName(folder)}: native loading {files.Count} frame(s).");
                    var imgs = await LoadImageBatchAsync(files, stageLabel, ct, progress);
                    var resultPixels = IntegrateDarkFrames(imgs, plan.Configuration.Rejection, stageLabel, progress);

                    // Build master ImageData from first header
                    var master = new FitsImageIO.ImageData
                    {
                        Width = imgs[0].Width,
                        Height = imgs[0].Height,
                        Channels = imgs[0].Channels,
                        Pixels = resultPixels,
                        Headers = new Dictionary<string, string>(imgs[0].Headers, StringComparer.OrdinalIgnoreCase)
                    };
                    master.Headers["IMAGETYP"] = "Master Dark";

                    // Write master and manifest atomically
                    Directory.CreateDirectory(tmpDir);
                    var outputExt = NormalizeOutputExtension(plan.Configuration.OutputFileExtension);
                    var tmpMaster = Path.Combine(tmpDir, $"MasterDark.{outputExt}");
                    await WriteImageAsync(tmpMaster, master, outputExt, cancellationToken);

                    var manifest = new MasterDarkManifest
                    {
                        PipelineVersion = CurrentMasterDarkPipelineVersion,
                        Key = keyHash,
                        ExposureSeconds = exposure,
                        CameraId = null,
                        Binning = binning,
                        Gain = gain,
                        Width = imgs[0].Width,
                        Height = imgs[0].Height,
                        TemperatureMedianC = normalizedTempMedian,
                        SourceFrames = [.. files.Select(f => new SourceFrameInfo { Path = f, LastWriteUtc = File.GetLastWriteTimeUtc(f) })]
                    };

                    var tmpMeta = Path.Combine(tmpDir, "MasterDark.meta.json");
                    await WriteManifestAsync(tmpMeta, manifest, cancellationToken);

                    // Move into final location atomically
                    Directory.CreateDirectory(outDir);
                    var finalMaster = masterPath;
                    var finalMeta = metaPath;
                    try
                    {
                        File.Copy(tmpMaster, finalMaster, overwrite: true);
                        File.Copy(tmpMeta, finalMeta, overwrite: true);
                        _logger.LogInformation("Created master dark: {Path}", finalMaster);
                        Report($"[MasterDark] Created master: {finalMaster}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed writing master to final location {FinalMaster} (tmp: {TmpMaster})", finalMaster, tmpMaster);
                        throw;
                    }
                    RegisterMasterInPlan(createdBag, planLock, plan, finalMaster, exposure, binning, gain, normalizedTempMedian);
                }
                finally
                {
                    try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Master dark materialization for folder '{Folder}' failed: {Msg}", folder, ex.Message);
                Report($"[MasterDark] ERROR in {folder}: {ex.Message}");
                failures.Add($"{folder}: {ex.Message}");
                // Continue to collect all failures and throw once processing completes.
                return;
            }
        }

        await ProcessMaterializationQueueAsync(requiredFolders, preferNative, cancellationToken, ProcessFolderAsync);

        if (!failures.IsEmpty)
        {
            var failed = failures.ToList();
            var preview = string.Join("; ", failed.Take(3));
            throw new InvalidOperationException($"Master dark materialization failed for {failed.Count} folder(s). First errors: {preview}");
        }

        return [.. createdBag];
    }

    public async Task<List<MaterializationCandidate>> PreviewMaterializationAsync(ProcessingPlan plan, IEnumerable<string> darkRoots, CancellationToken cancellationToken = default)
    {
        var candidates = new List<MaterializationCandidate>();
        var required = DetectRequiredFolders(plan, darkRoots);
        foreach (var kv in required)
        {
            var folder = kv.Key;
            // Include files from nested subfolders so preview reflects actual raw frames
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Exclude files that are already master dark artifacts (do not count them as raw frames)
            var filteredFiles = files.Where(f =>
                {
                    if (LooksLikeMasterArtifact(Path.GetFileName(f)))
                        return false;
                    // Also exclude any file that appears in the plan's DarkCatalog as a master
                    try
                    {
                        var entry = plan.DarkCatalog.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase));
                        if (entry != null && (entry.Type == FlatMaster.Core.Models.ImageType.MasterDark || entry.Type == FlatMaster.Core.Models.ImageType.MasterDarkFlat || entry.Type == FlatMaster.Core.Models.ImageType.MasterBias || entry.Type == FlatMaster.Core.Models.ImageType.MasterFlat))
                            return false;
                    }
                    catch { }
                    return true;
                }).ToList();

            double? exposure = null;
            try
            {
                // Prefer folder-name parse but fall back to metadata median when available
                var name = Path.GetFileName(folder);
                exposure = FlatMaster.Core.Utilities.MasterDarkUtilities.ExtractExposureFromFolderName(name);

                if (!exposure.HasValue && filteredFiles.Count > 0)
                {
                    var metaBatch = await _metadataReader.ReadMetadataBatchAsync(filteredFiles, cancellationToken);
                    var exposures = metaBatch.Values.Where(m => m.ExposureTime.HasValue).Select(m => m.ExposureTime!.Value).ToList();
                    if (exposures.Count > 0)
                    {
                        exposure = exposures.OrderBy(e => e).ElementAt(exposures.Count / 2);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Preview: failed reading metadata for folder {Folder}", folder);
            }

            candidates.Add(new MaterializationCandidate { Folder = folder, ExposureSeconds = exposure, FrameCount = filteredFiles.Count });
            _logger.LogInformation("PreviewMaterialization: folder={Folder} rawFrames={Frames} totalFiles={Total}", folder, filteredFiles.Count, files.Count);
        }

        return candidates;
    }

    public Task<List<MaterializationCandidate>> PreviewDarksOnlyMaterializationAsync(
        ProcessingPlan plan,
        double temperatureToleranceC = 1.0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buckets = BuildDarksOnlyBuckets(plan.DarkCatalog, temperatureToleranceC);
        var result = buckets
            .OrderBy(b => b.ExposureSeconds)
            .ThenBy(b => b.TemperatureC ?? double.PositiveInfinity)
            .Select(b => new MaterializationCandidate
            {
                Folder = b.SourceLabel,
                ExposureSeconds = b.ExposureSeconds,
                TemperatureC = b.TemperatureC,
                FrameCount = b.FilePaths.Count
            })
            .ToList();

        return Task.FromResult(result);
    }

    public async Task<List<string>> MaterializeDarksOnlyAsync(
        ProcessingPlan plan,
        bool preferNative,
        string? pixInsightExecutable,
        double temperatureToleranceC = 1.0,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        void Report(string message)
        {
            _logger.LogInformation("{Message}", message);
            progress?.Report(message);
        }

        ArgumentNullException.ThrowIfNull(plan);
        var outputRoot = plan.Jobs.FirstOrDefault()?.OutputRootPath;
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new InvalidOperationException("Output root is required for darks-only materialization.");

        var buckets = BuildDarksOnlyBuckets(plan.DarkCatalog, temperatureToleranceC);
        if (buckets.Count == 0)
        {
            _logger.LogInformation("Darks-only materialization: no eligible raw dark groups found.");
            Report("[DarksOnly] No eligible raw dark groups found.");
            return [];
        }
        Report($"[DarksOnly] Building {buckets.Count} dark bucket(s).");

        var createdBag = new ConcurrentBag<string>();
        var failures = new ConcurrentBag<string>();
        var planLock = new object();

        async Task ProcessBucketAsync(DarksOnlyBucket bucket, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                Report($"[DarksOnly] Processing bucket: exp {bucket.ExposureSeconds:0.###}s, temp {(bucket.TemperatureC?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unknown")} degC ({bucket.FilePaths.Count} frames).");

                var files = bucket.FilePaths;
                if (files.Count < 3)
                {
                    _logger.LogWarning("Skipping dark bucket exp={Exposure:F3}s temp={Temp}: only {Count} frame(s), need >=3.",
                        bucket.ExposureSeconds, bucket.TemperatureC?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unknown", files.Count);
                    Report($"[DarksOnly] Skipping bucket exp {bucket.ExposureSeconds:0.###}s temp {(bucket.TemperatureC?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unknown")} degC: only {files.Count} frame(s).");
                    return;
                }

                var outDir = MasterDarkPathing.BuildMasterDarkOutputDirectory(outputRoot, bucket.ExposureSeconds, bucket.TemperatureC);
                Directory.CreateDirectory(outDir);
                var masterPath = Path.Combine(
                    outDir,
                    MasterDarkPathing.BuildMasterDarkFileName(bucket.ExposureSeconds, bucket.TemperatureC, plan.Configuration.OutputFileExtension));
                var metaPath = Path.Combine(outDir, "MasterDark.meta.json");

                int width = 0, height = 0;
                try
                {
                    var fitsInfo = new FitsImageIO(_logger);
                    var info = await fitsInfo.ReadAsync(files[0], ct);
                    width = info.Width;
                    height = info.Height;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed reading image dimensions from {File}", files[0]);
                }

                var keyHash = FlatMaster.Core.Utilities.MasterDarkUtilities.ComputeMasterKeyHash(
                    bucket.ExposureSeconds,
                    bucket.Binning,
                    bucket.Gain,
                    width,
                    height);

                if (await CanReuseExistingMasterAsync(masterPath, metaPath, keyHash, ct))
                {
                    Report($"[DarksOnly] Reusing existing master: {masterPath}");
                    RegisterMasterInPlan(createdBag, planLock, plan, masterPath, bucket.ExposureSeconds, bucket.Binning, bucket.Gain, bucket.TemperatureC);
                    return;
                }

                if (!preferNative)
                {
                    if (string.IsNullOrWhiteSpace(pixInsightExecutable) || !File.Exists(pixInsightExecutable))
                        throw new InvalidOperationException("PixInsight mode selected but PixInsight executable not found.");
                    var request = new PixInsightMaterializeRequest
                    {
                        SourceLabel = bucket.SourceLabel,
                        OutputRoot = outputRoot,
                        OutputDirectory = outDir,
                        FilePaths = files,
                        ExposureSeconds = bucket.ExposureSeconds,
                        Binning = bucket.Binning,
                        Gain = bucket.Gain,
                        TemperatureC = bucket.TemperatureC,
                        RepresentativeMetadata = null,
                        PixInsightExecutable = pixInsightExecutable!,
                        ErrorContext = $"bucket {bucket.SourceLabel}"
                    };
                    Report($"[DarksOnly] Invoking PixInsight integration for {files.Count} frames.");
                    await MaterializeWithPixInsightAsync(plan, request, masterPath, ct, progress);
                }
                else
                {
                    var stageLabel = $"Darks-only bucket ({bucket.ExposureSeconds:0.###}s, {(bucket.TemperatureC?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unknown")}C)";
                    var imgs = await LoadImageBatchAsync(files, stageLabel, ct, progress);
                    var resultPixels = IntegrateDarkFrames(imgs, plan.Configuration.Rejection, stageLabel, progress);

                    var master = new FitsImageIO.ImageData
                    {
                        Width = imgs[0].Width,
                        Height = imgs[0].Height,
                        Channels = imgs[0].Channels,
                        Pixels = resultPixels,
                        Headers = new Dictionary<string, string>(imgs[0].Headers, StringComparer.OrdinalIgnoreCase)
                    };
                    master.Headers["IMAGETYP"] = "Master Dark";
                    var outputExt = NormalizeOutputExtension(plan.Configuration.OutputFileExtension);
                    await WriteImageAsync(masterPath, master, outputExt, ct);
                }

                var manifest = new MasterDarkManifest
                {
                    PipelineVersion = CurrentMasterDarkPipelineVersion,
                    Key = keyHash,
                    ExposureSeconds = bucket.ExposureSeconds,
                    CameraId = null,
                    Binning = bucket.Binning,
                    Gain = bucket.Gain,
                    Width = width,
                    Height = height,
                    TemperatureMedianC = bucket.TemperatureC,
                    SourceFrames = [.. files.Select(f => new SourceFrameInfo { Path = f, LastWriteUtc = File.GetLastWriteTimeUtc(f) })]
                };
                await WriteManifestAsync(metaPath, manifest, ct);

                RegisterMasterInPlan(createdBag, planLock, plan, masterPath, bucket.ExposureSeconds, bucket.Binning, bucket.Gain, bucket.TemperatureC);
                Report($"[DarksOnly] Created master: {masterPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Darks-only materialization failed for bucket {Bucket}: {Message}", bucket.SourceLabel, ex.Message);
                Report($"[DarksOnly] ERROR in bucket {bucket.SourceLabel}: {ex.Message}");
                failures.Add($"{bucket.SourceLabel}: {ex.Message}");
            }
        }

        await ProcessMaterializationQueueAsync(buckets, preferNative, cancellationToken, ProcessBucketAsync);

        if (!failures.IsEmpty)
        {
            var failed = failures.ToList();
            var preview = string.Join("; ", failed.Take(3));
            throw new InvalidOperationException($"Darks-only materialization failed for {failed.Count} group(s). First errors: {preview}");
        }

        return [.. createdBag];
    }

    private async Task MaterializeWithPixInsightAsync(
        ProcessingPlan plan,
        PixInsightMaterializeRequest request,
        string expectedMasterPath,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var darkJob = BuildPixInsightDarkJob(request);
        var batchPlan = new ProcessingPlan
        {
            Jobs = [darkJob],
            DarkCatalog = [.. plan.DarkCatalog],
            Configuration = plan.Configuration
        };

        var pixInsightProgress = new Progress<string>(s =>
        {
            _logger.LogInformation("[PixInsight] {Msg}", s);
            progress?.Report($"[MasterDark][PixInsight] {s}");
        });
        var result = await _pixInsight.ProcessJobsInBatchesAsync(batchPlan, request.PixInsightExecutable, 1, pixInsightProgress, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"PixInsight failed creating master dark for {request.ErrorContext}: {result.ErrorMessage}");

        _logger.LogInformation("PixInsight path: expecting master at {MasterPath}", expectedMasterPath);
        if (!File.Exists(expectedMasterPath))
            throw new InvalidOperationException($"PixInsight did not produce expected master at {expectedMasterPath}");

        _logger.LogInformation("PixInsight created master dark: {Path}", expectedMasterPath);
    }

    private static DirectoryJob BuildPixInsightDarkJob(PixInsightMaterializeRequest request)
    {
        var relOutDir = Path.GetRelativePath(request.OutputRoot, request.OutputDirectory).Replace('\\', '/');
        if (relOutDir.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Computed master dark output path is outside output root: {request.OutputDirectory}");

        return new DirectoryJob
        {
            DirectoryPath = request.SourceLabel,
            BaseRootPath = request.SourceLabel,
            OutputRootPath = request.OutputRoot,
            RelativeDirectory = "__DARKMATERIALIZE__/" + relOutDir.TrimStart('/'),
            ExposureGroups =
            [
                new ExposureGroup
                {
                    ExposureTime = request.ExposureSeconds,
                    FilePaths = request.FilePaths,
                    RepresentativeMetadata = request.RepresentativeMetadata,
                    MatchingCriteria = new MatchingCriteria
                    {
                        Binning = request.Binning,
                        Gain = request.Gain,
                        Temperature = request.TemperatureC
                    }
                }
            ]
        };
    }

    private async Task<FitsImageIO.ImageData[]> LoadImageBatchAsync(
        IReadOnlyList<string> files,
        string stageLabel,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        if (files.Count == 0)
            return [];

        var workers = Math.Max(1, Math.Min(Environment.ProcessorCount, files.Count));
        var reportEvery = Math.Max(1, files.Count / 10);
        var loaded = 0;
        var images = new FitsImageIO.ImageData[files.Count];

        _logger.LogInformation(
            "{Stage}: loading {Count} frame(s) with {Workers} worker(s).",
            stageLabel,
            files.Count,
            workers);
        progress?.Report($"[MasterDark] {stageLabel}: loading {files.Count} frame(s) with {workers} worker(s).");

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), options, async (index, ct) =>
        {
            var io = new FitsImageIO(_logger);
            images[index] = await io.ReadAsync(files[index], ct);
            var done = Interlocked.Increment(ref loaded);
            if (done % reportEvery == 0 || done == files.Count)
            {
                _logger.LogInformation(
                    "{Stage}: loaded {Done}/{Total} ({File})",
                    stageLabel,
                    done,
                    files.Count,
                    Path.GetFileName(files[index]));
                progress?.Report($"[MasterDark] {stageLabel}: loaded {done}/{files.Count} ({Path.GetFileName(files[index])})");
            }
        });

        return images;
    }

    private double[] IntegrateDarkFrames(
        IReadOnlyList<FitsImageIO.ImageData> images,
        RejectionSettings rejection,
        string stageLabel,
        IProgress<string>? progress = null)
    {
        if (images.Count == 0)
            throw new InvalidOperationException("Cannot integrate an empty dark frame set.");

        var n = images.Count;
        var frames = images as FitsImageIO.ImageData[] ?? [.. images];
        long pixelCount = (long)frames[0].Width * frames[0].Height * frames[0].Channels;
        var resultPixels = new double[pixelCount];

        if (n < 3)
        {
            _logger.LogInformation("{Stage}: integrating {Count} frame(s) using Average.", stageLabel, n);
            progress?.Report($"[MasterDark] {stageLabel}: integrating {n} frame(s) using Average.");
            ImageStackingAlgorithms.AverageStack(frames, resultPixels, pixelCount);
        }
        else if (n < 6)
        {
            _logger.LogInformation("{Stage}: integrating {Count} frame(s) using Percentile Clip.", stageLabel, n);
            progress?.Report($"[MasterDark] {stageLabel}: integrating {n} frame(s) using Percentile Clip.");
            ImageStackingAlgorithms.PercentileClipStack(frames, resultPixels, pixelCount, 0.20, 0.10, null);
        }
        else
        {
            _logger.LogInformation(
                "{Stage}: integrating {Count} frame(s) using Winsorized Sigma Clip (low={LowSigma:F1}, high={HighSigma:F1}).",
                stageLabel,
                n,
                rejection.LowSigma,
                rejection.HighSigma);
            progress?.Report($"[MasterDark] {stageLabel}: integrating {n} frame(s) using Winsorized Sigma Clip.");
            ImageStackingAlgorithms.WinsorizedSigmaClipStack(frames, resultPixels, pixelCount, rejection.LowSigma, rejection.HighSigma, 5.0, 10, null);
        }

        return resultPixels;
    }

    private static string NormalizeOutputExtension(string? extension)
    {
        if (string.Equals(extension, "fits", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, "fit", StringComparison.OrdinalIgnoreCase))
            return "fits";

        return "xisf";
    }

    private static Task WriteImageAsync(string path, FitsImageIO.ImageData image, string outputExt, CancellationToken ct)
    {
        return outputExt == "fits"
            ? FitsImageIO.WriteFitsAsync(path, image, ct)
            : FitsImageIO.WriteXisfAsync(path, image, ct);
    }

    private static async Task ProcessMaterializationQueueAsync<T>(
        IEnumerable<T> workItems,
        bool runInParallel,
        CancellationToken cancellationToken,
        Func<T, CancellationToken, Task> worker)
    {
        if (runInParallel)
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = cancellationToken
            };
            await Parallel.ForEachAsync(workItems, options, async (item, ct) => await worker(item, ct));
            return;
        }

        foreach (var item in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await worker(item, cancellationToken);
        }
    }

    private Dictionary<string, List<(DirectoryJob job, ExposureGroup group)>> DetectRequiredFolders(ProcessingPlan plan, IEnumerable<string> darkRoots)
    {
        var requiredFolders = new Dictionary<string, List<(DirectoryJob job, ExposureGroup group)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in plan.Jobs)
        {
            foreach (var group in job.ExposureGroups)
            {
                var match = _darkMatcher.FindBestDark(group, plan.DarkCatalog, plan.Configuration.DarkMatching);
                if (match == null) continue;

                var matchedPath = match.FilePath;
                if (string.IsNullOrWhiteSpace(matchedPath)) continue;

                var catalogEntry = plan.DarkCatalog.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(matchedPath), StringComparison.OrdinalIgnoreCase));
                if (catalogEntry == null)
                    continue;

                // Only materialize loose raw dark matches (priorities 2/4/6).
                if (catalogEntry.Type is not ImageType.Dark and not ImageType.DarkFlat)
                    continue;

                // Group strictly by DARKSXXX/DARKXXX root folder and ignore deeper hierarchy.
                var parent = catalogEntry.DarkGroupFolder;
                if (string.IsNullOrWhiteSpace(parent))
                    parent = FileScannerService.FindDarksAncestor(matchedPath);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    _logger.LogDebug("Skipping loose dark outside DARKSXXX/DARKXXX hierarchy: {Path}", matchedPath);
                    continue;
                }

                if (!requiredFolders.ContainsKey(parent))
                    requiredFolders[parent] = [];
                requiredFolders[parent].Add((job, group));
            }
        }

        return requiredFolders;
    }

    private static List<DarksOnlyBucket> BuildDarksOnlyBuckets(IEnumerable<DarkFrame> darkCatalog, double temperatureToleranceC)
    {
        var tolerance = Math.Max(0.0, temperatureToleranceC);
        var rawDarks = darkCatalog
            .Where(d => d.Type is ImageType.Dark or ImageType.DarkFlat)
            .Where(d => !string.IsNullOrWhiteSpace(d.FilePath) && File.Exists(d.FilePath))
            .ToList();

        var result = new List<DarksOnlyBucket>();
        var grouped = rawDarks.GroupBy(d => new
        {
            Exposure = Math.Round(d.ExposureTime, 3, MidpointRounding.AwayFromZero),
            Binning = string.IsNullOrWhiteSpace(d.Binning) ? "1" : d.Binning!.Trim(),
            Gain = d.Gain.HasValue ? Math.Round(d.Gain.Value, 3, MidpointRounding.AwayFromZero) : double.NaN
        });

        foreach (var g in grouped)
        {
            var known = g.Where(d => d.Temperature.HasValue).OrderBy(d => d.Temperature!.Value).ToList();
            var unknown = g.Where(d => !d.Temperature.HasValue).ToList();

            var clusters = new List<List<DarkFrame>>();
            foreach (var frame in known)
            {
                var temp = frame.Temperature!.Value;
                List<DarkFrame>? target = null;
                var bestDelta = double.MaxValue;
                foreach (var cluster in clusters)
                {
                    var center = cluster.Average(x => x.Temperature!.Value);
                    var delta = Math.Abs(temp - center);
                    if (delta <= tolerance && delta < bestDelta)
                    {
                        target = cluster;
                        bestDelta = delta;
                    }
                }

                if (target == null)
                {
                    target = [];
                    clusters.Add(target);
                }
                target.Add(frame);
            }

            foreach (var cluster in clusters)
            {
                var files = cluster.Select(x => x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var tempMedian = cluster.Select(x => x.Temperature!.Value).OrderBy(x => x).ElementAt(cluster.Count / 2);
                var label = cluster.Select(x => x.DarkGroupFolder).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    ?? Path.GetDirectoryName(files.First()) ?? "DARKS";

                result.Add(new DarksOnlyBucket
                {
                    SourceLabel = label,
                    ExposureSeconds = g.Key.Exposure,
                    TemperatureC = tempMedian,
                    Binning = g.Key.Binning,
                    Gain = double.IsNaN(g.Key.Gain) ? null : g.Key.Gain,
                    FilePaths = files
                });
            }

            if (unknown.Count > 0)
            {
                var files = unknown.Select(x => x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var label = unknown.Select(x => x.DarkGroupFolder).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    ?? Path.GetDirectoryName(files.First()) ?? "DARKS";

                result.Add(new DarksOnlyBucket
                {
                    SourceLabel = label,
                    ExposureSeconds = g.Key.Exposure,
                    TemperatureC = null,
                    Binning = g.Key.Binning,
                    Gain = double.IsNaN(g.Key.Gain) ? null : g.Key.Gain,
                    FilePaths = files
                });
            }
        }

        return result;
    }

    private sealed class DarksOnlyBucket
    {
        public required string SourceLabel { get; init; }
        public required double ExposureSeconds { get; init; }
        public required double? TemperatureC { get; init; }
        public required string Binning { get; init; }
        public required double? Gain { get; init; }
        public required List<string> FilePaths { get; init; }
    }

    private static async Task<bool> CanReuseExistingMasterAsync(
        string masterPath,
        string manifestPath,
        string expectedKeyHash,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(masterPath) || !File.Exists(manifestPath))
                return false;

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<MasterDarkManifest>(manifestJson);
            if (manifest == null ||
                manifest.PipelineVersion != CurrentMasterDarkPipelineVersion ||
                !string.Equals(manifest.Key, expectedKeyHash, StringComparison.Ordinal))
                return false;

            foreach (var src in manifest.SourceFrames)
            {
                if (!File.Exists(src.Path) || File.GetLastWriteTimeUtc(src.Path) != src.LastWriteUtc)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Task WriteManifestAsync(string manifestPath, MasterDarkManifest manifest, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        return File.WriteAllTextAsync(manifestPath, json, cancellationToken);
    }

    private static void RegisterMasterInPlan(
        ConcurrentBag<string> createdBag,
        object planLock,
        ProcessingPlan plan,
        string filePath,
        double exposureSeconds,
        string? binning,
        double? gain,
        double? temperatureC)
    {
        createdBag.Add(filePath);
        lock (planLock)
        {
            plan.DarkCatalog.Add(new DarkFrame
            {
                FilePath = filePath,
                Type = ImageType.MasterDark,
                ExposureTime = exposureSeconds,
                Binning = binning,
                Gain = gain,
                Temperature = temperatureC
            });
        }
    }

    private static bool IsRawDarkInputFile(
        string filePath,
        IReadOnlyDictionary<string, ImageMetadata> metadata,
        IReadOnlyList<DarkFrame> catalog)
    {
        if (LooksLikeMasterArtifact(Path.GetFileName(filePath)))
            return false;

        if (metadata.TryGetValue(filePath, out var m))
        {
            if (m.Type is ImageType.Dark or ImageType.DarkFlat)
                return true;

            if (m.Type is ImageType.MasterDark or ImageType.MasterDarkFlat or ImageType.Bias or ImageType.MasterBias or ImageType.Flat or ImageType.MasterFlat or ImageType.Light)
                return false;
        }

        try
        {
            var entry = catalog.FirstOrDefault(d =>
                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                return entry.Type is ImageType.Dark or ImageType.DarkFlat;
        }
        catch
        {
            // Best effort fallback.
        }

        return true;
    }

    private static bool LooksLikeMasterArtifact(string fileName)
    {
        var upper = fileName.ToUpperInvariant();
        return upper.Contains("MASTERDARK")
            || upper.Contains("MASTER_DARK")
            || upper.Contains("MASTERBIAS")
            || upper.Contains("MASTER_BIAS")
            || upper.Contains("MASTERFLAT")
            || upper.Contains("MASTER_FLAT");
    }
}



