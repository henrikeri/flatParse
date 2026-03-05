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
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Concurrent;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Native (non-PixInsight) flat calibration and integration engine.
/// Performs: dark subtraction, Winsorized Sigma Clipping rejection, and average combination.
/// Output is Float64 XISF, matching PixInsight's 64-bit master output precision.
/// </summary>
public sealed partial class NativeProcessingService(
    ILogger<NativeProcessingService> logger,
    IDarkMatchingService darkMatcher) : IImageProcessingEngine
{
    private static readonly Regex FilterRegex = MyRegex();

    private readonly ILogger<NativeProcessingService> _logger = logger;
    private readonly IDarkMatchingService _darkMatcher = darkMatcher;

    public async Task<ProcessingResult> ExecuteAsync(
        ProcessingPlan plan,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var io = new FitsImageIO(_logger);
        _logger.LogInformation("Native Processing Engine started");
        progress.Report("=== Native Processing Engine ===");
        progress.Report($"Native parallel workers available: {Environment.ProcessorCount}");

        if (plan.Jobs.Count == 0)
        {
            return new ProcessingResult
            {
                Success = false,
                ErrorMessage = "No jobs in plan",
                Output = "Validation failed: No jobs",
                ExitCode = 1
            };
        }

        int successCount = 0;
        int failureCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var job in plan.Jobs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            progress.Report($"\n-- Job: {job.DirectoryPath} --");

            foreach (var group in job.ExposureGroups)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    progress.Report(string.Format(
                        CultureInfo.InvariantCulture,
                        "  Resolving dark match for {0:F3}s ({1} frames)...",
                        group.ExposureTime,
                        group.FilePaths.Count));

                    var darkMatch = _darkMatcher.FindBestDark(
                        group, plan.DarkCatalog, plan.Configuration.DarkMatching);

                    if (darkMatch == null)
                    {
                        if (plan.Configuration.RequireDarks)
                        {
                            progress.Report(string.Format(CultureInfo.InvariantCulture, "  ! No dark/bias for {0:F3}s - skipped", group.ExposureTime));
                            failureCount++;
                            continue;
                        }

                        progress.Report(string.Format(CultureInfo.InvariantCulture, "  ! No dark/bias for {0:F3}s - integrating flats without subtraction", group.ExposureTime));
                    }
                    else
                    {
                        progress.Report($"  Dark selected: {Path.GetFileName(darkMatch.FilePath)} [{darkMatch.MatchKind}]");
                        progress.Report($"  Dark source: {darkMatch.FilePath}");
                        progress.Report($"  Dark optimize required: {(darkMatch.OptimizeRequired ? "yes" : "no")}");
                    }

                    await ProcessGroupAsync(
                        io, group, darkMatch, job, plan.Configuration,
                        progress, cancellationToken);

                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed group {Exp}s", group.ExposureTime);
                    progress.Report($"  ERROR: {ex.Message}");
                    failureCount++;
                }
            }
        }

        sw.Stop();
        var summary = string.Format(CultureInfo.InvariantCulture, "Done in {0:F1}s - {1} succeeded, {2} failed", sw.Elapsed.TotalSeconds, successCount, failureCount);
        progress.Report($"\n{summary}");

        return new ProcessingResult
        {
            Success = failureCount == 0,
            Output = summary,
            ExitCode = failureCount == 0 ? 0 : 1
        };
    }

    // ---------------------- Per-group pipeline ----------------------

    private static async Task ProcessGroupAsync(
        FitsImageIO io,
        ExposureGroup group,
        DarkMatchResult? darkMatch,
        DirectoryJob job,
        ProcessingConfiguration config,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Sort file paths for deterministic processing order (matches PI's filesystem-order enumeration)
        var flatPaths = group.FilePaths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        int n = flatPaths.Count;
        int frameWorkers = Math.Max(1, Math.Min(Environment.ProcessorCount, n));
        int reportEvery = Math.Max(1, n / 10);
        progress.Report(string.Format(CultureInfo.InvariantCulture, "  Loading {0} flat frames ({1:F3}s)...", n, group.ExposureTime));
        progress.Report($"  Native prepare stage: parallel workers={frameWorkers}");

        // 1. Load dark/bias frame when available.
        FitsImageIO.ImageData? darkImage = null;
        if (darkMatch != null)
        {
            progress.Report("  Reading selected dark/bias frame...");
            darkImage = await io.ReadAsync(darkMatch.FilePath, ct);
            progress.Report($"  Dark loaded: {darkImage.Width}x{darkImage.Height}");

            if (darkMatch.OptimizeRequired)
            {
                // Scale the dark by the exposure ratio  (flatExp / darkExp)
                double darkExp = ParseExposure(darkImage.Headers);
                if (darkExp > 0 && group.ExposureTime > 0)
                {
                    double scale = group.ExposureTime / darkExp;
                    progress.Report(string.Format(CultureInfo.InvariantCulture, "  Optimizing dark: scale x{0:F4} ({1:F3}s -> {2:F3}s)", scale, darkExp, group.ExposureTime));
                    ScalePixels(darkImage.Pixels, scale);
                }
            }
        }

        // 2. Load and calibrate each flat (subtract dark/bias when available)
        var calibrated = new FitsImageIO.ImageData[n];
        int calibratedCount = 0;
        var calibrationOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = frameWorkers,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, n), calibrationOptions, async (i, pct) =>
        {
            var flat = await io.ReadAsync(flatPaths[i], pct);
            if (darkImage != null)
            {
                ValidateDimensions(flat, darkImage, flatPaths[i]);
                SubtractDark(flat.Pixels, darkImage.Pixels);
            }
            calibrated[i] = flat;
            int done = Interlocked.Increment(ref calibratedCount);
            if (done % reportEvery == 0 || done == n)
                progress.Report($"  Calibrated {done}/{n} (latest: {Path.GetFileName(flatPaths[i])})");
        });

        // 3. Normalise to multiplicative (divide each frame by its median - matches PI)
        progress.Report($"  Normalising (multiplicative, median-based) with {frameWorkers} workers...");
        var medians = new double[n];
        int normalizedCount = 0;
        Parallel.For(0, n, calibrationOptions, i =>
        {
            medians[i] = ComputeMedian(calibrated[i].Pixels);
            if (Math.Abs(medians[i]) > 1e-15)
                DividePixels(calibrated[i].Pixels, medians[i]);
            int done = Interlocked.Increment(ref normalizedCount);
            if (done % reportEvery == 0 || done == n)
                progress.Report($"  Normalized {done}/{n}");
        });
        progress.Report(string.Format(CultureInfo.InvariantCulture,
            "  Frame medians: [{0}]", string.Join(", ", medians.Select(m => m.ToString("F6", CultureInfo.InvariantCulture)))));

        // 3b. Compute EqualizeFluxes rejection-normalization factors
        //     After multiplicative normalization, each frame's median ~ 1.0.
        //     EqualizeFluxes additionally scales by refMean/frameMean so rejection
        //     testing uses equalized pixel values - matches PI's rejectionNormalization = EqualizeFluxes.
        var eqFactors = new double[n];
        double refNormMean = ComputeMean(calibrated[0].Pixels);
        Parallel.For(0, n, calibrationOptions, i =>
        {
            double frameMean = ComputeMean(calibrated[i].Pixels);
            eqFactors[i] = (Math.Abs(frameMean) > 1e-15) ? refNormMean / frameMean : 1.0;
        });

        // 4. Integrate
        var rej = config.Rejection;
        progress.Report(string.Format(CultureInfo.InvariantCulture, "  Integrating frames (sigma_low={0:F1}, sigma_high={1:F1})...", rej.LowSigma, rej.HighSigma));

        long pixelCount = (long)calibrated[0].Width * calibrated[0].Height * calibrated[0].Channels;
        var result = new double[pixelCount];

        // Select rejection strategy based on frame count (mirrors the PJSR template)
        if (n < 3)
        {
            // Straight average, no rejection possible
            progress.Report("  Integration algorithm: Average");
            ImageStackingAlgorithms.AverageStack(calibrated, result, pixelCount);
        }
        else if (n < 6)
        {
            // Percentile clip for small stacks
            progress.Report("  Integration algorithm: Percentile Clip (small stack)");
            ImageStackingAlgorithms.PercentileClipStack(calibrated, result, pixelCount, 0.20, 0.10, eqFactors);
        }
        else if (n <= 15)
        {
            // Winsorized Sigma Clipping for medium stacks
            progress.Report("  Integration algorithm: Winsorized Sigma Clip");
            ImageStackingAlgorithms.WinsorizedSigmaClipStack(calibrated, result, pixelCount,
                rej.LowSigma, rej.HighSigma, winsorizationCutoff: 5.0, maxIterations: 10, eqFactors);
        }
        else
        {
            // WBPP-style large-stack strategy for flats.
            const double linearFitLow = 5.0;
            const double linearFitHigh = 3.5;
            progress.Report("  Integration algorithm: Linear Fit Clipping (large stack)");
            var (fitSlopes, fitIntercepts) = ComputeLinearFitTransforms(calibrated, eqFactors);
            ImageStackingAlgorithms.LinearFitSigmaClipStack(
                calibrated,
                result,
                pixelCount,
                linearFitLow,
                linearFitHigh,
                maxIterations: 10,
                eqFactors,
                fitSlopes,
                fitIntercepts);
        }

        // 5. Rescale result by reference median (first frame) - matches PI multiplicative normalization
        double referenceMedian = medians[0];
        if (Math.Abs(referenceMedian) > 1e-15)
        {
            Parallel.ForEach(Partitioner.Create(0L, pixelCount), range =>
            {
                for (long p = range.Item1; p < range.Item2; p++)
                    result[p] *= referenceMedian;
            });
        }

        // 6. Build output image
        var master = new FitsImageIO.ImageData
        {
            Width = calibrated[0].Width,
            Height = calibrated[0].Height,
            Channels = calibrated[0].Channels,
            Pixels = result,
            Headers = new Dictionary<string, string>(calibrated[0].Headers, StringComparer.OrdinalIgnoreCase)
        };
        master.Headers["IMAGETYP"] = "Master Flat";

        // 7. Determine output path
        var filter = group.RepresentativeMetadata?.Filter ?? GuessFilter(flatPaths, job.DirectoryPath);
        var binning = group.MatchingCriteria?.Binning ?? "1";
        var date = GuessDate(job.DirectoryPath);
        var outputExt = NormalizeOutputExtension(config.OutputFileExtension);
        var masterName = string.Format(CultureInfo.InvariantCulture, "MasterFlat_{0}_{1}_Bin{2}_{3:F3}s.{4}", date, filter, binning, group.ExposureTime, outputExt);

        var outDir = job.OutputRootPath;
        if (!string.IsNullOrEmpty(job.RelativeDirectory) && job.RelativeDirectory != ".")
            outDir = Path.Combine(outDir, job.RelativeDirectory);
        var masterPath = Path.Combine(outDir, masterName);

        progress.Report($"  Writing: {masterPath}");
        await WriteImageAsync(masterPath, master, outputExt, ct);
        progress.Report($"  [OK] Master flat written ({master.Width}x{master.Height}, {n} frames integrated)");
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

    // Pixel Math Helpers
    private static void SubtractDark(double[] flat, double[] dark)
    {
        int len = Math.Min(flat.Length, dark.Length);
        for (int i = 0; i < len; i++)
            flat[i] -= dark[i];  // Allow negative values (matches PI ImageCalibration)
    }

    private static void ScalePixels(double[] pixels, double factor)
    {
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] *= factor;
    }

    private static void DividePixels(double[] pixels, double divisor)
    {
        double inv = 1.0 / divisor;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] *= inv;
    }

    private static double ComputeMean(double[] pixels)
    {
        double sum = 0;
        for (int i = 0; i < pixels.Length; i++)
            sum += pixels[i];
        return sum / pixels.Length;
    }

    private static (double[] Slopes, double[] Intercepts) ComputeLinearFitTransforms(
        FitsImageIO.ImageData[] frames,
        double[] eqFactors)
    {
        int n = frames.Length;
        var slopes = new double[n];
        var intercepts = new double[n];
        slopes[0] = 1.0;
        intercepts[0] = 0.0;

        long pixelCount = frames[0].Pixels.LongLength;
        long targetSamples = 2_000_000;
        long step = Math.Max(1, pixelCount / targetSamples);
        double refEqFactor = eqFactors.Length > 0 ? eqFactors[0] : 1.0;
        var referencePixels = frames[0].Pixels;

        Parallel.For(1, n, i =>
        {
            var framePixels = frames[i].Pixels;
            double eq = eqFactors[i];
            double sumX = 0;
            double sumY = 0;
            double sumXX = 0;
            double sumXY = 0;
            long count = 0;

            for (long p = 0; p < pixelCount; p += step)
            {
                double x = framePixels[p] * eq;
                double y = referencePixels[p] * refEqFactor;
                sumX += x;
                sumY += y;
                sumXX += x * x;
                sumXY += x * y;
                count++;
            }

            double slope = 1.0;
            double intercept = 0.0;
            if (count > 0)
            {
                double denom = count * sumXX - sumX * sumX;
                if (Math.Abs(denom) > 1e-20)
                {
                    slope = (count * sumXY - sumX * sumY) / denom;
                    if (!double.IsFinite(slope) || Math.Abs(slope) < 1e-12)
                        slope = 1.0;
                }

                intercept = (sumY - slope * sumX) / count;
                if (!double.IsFinite(intercept))
                    intercept = 0.0;
            }

            slopes[i] = slope;
            intercepts[i] = intercept;
        });

        return (slopes, intercepts);
    }

    /// <summary>
    /// Computes the exact median using a 3-pass histogram refinement approach.
    /// Pass 1: find min/max range.  Pass 2: 1M-bin histogram to locate the median bin.
    /// Pass 3: collect and sort only the values in that bin for a precise result.
    /// O(n) time, ~4 MB histogram - no large array copy needed.
    /// </summary>
    private static double ComputeMedian(double[] pixels)
    {
        int len = pixels.Length;
        if (len <= 1) return len == 0 ? 0.0 : pixels[0];

        // Pass 1: find range
        double min = pixels[0], max = pixels[0];
        for (int i = 1; i < len; i++)
        {
            double v = pixels[i];
            if (v < min) min = v;
            else if (v > max) max = v;
        }
        if (max - min < 1e-20) return min;

        // Pass 2: histogram - locate the bin containing the median
        const int numBins = 1 << 20; // 1,048,576 bins
        double scale = (numBins - 1.0) / (max - min);
        var hist = new int[numBins];
        for (int i = 0; i < len; i++)
        {
            int b = (int)((pixels[i] - min) * scale);
            hist[b]++;
        }

        int target = len / 2; // 0-indexed position of upper-middle element
        long cum = 0;
        int medBin = 0;
        for (int b = 0; b < numBins; b++)
        {
            cum += hist[b];
            if (cum > target) { medBin = b; break; }
        }
        long preceding = cum - hist[medBin];

        // Pass 3: collect values in the median bin, sort, extract exact median
        var binVals = new double[hist[medBin]];
        int idx = 0;
        for (int i = 0; i < len; i++)
        {
            int b = (int)((pixels[i] - min) * scale);
            if (b == medBin)
                binVals[idx++] = pixels[i];
        }
        Array.Sort(binVals);

        int posInBin = (int)(target - preceding);

        if (len % 2 != 0)
            return binVals[posInBin];

        // Even length: median = average of elements at positions target-1 and target
        double upper = binVals[posInBin];
        double lower;
        if (posInBin > 0)
        {
            lower = binVals[posInBin - 1];
        }
        else
        {
            // target-1 element is the max of all preceding bins
            lower = double.MinValue;
            for (int i = 0; i < len; i++)
            {
                int b = (int)((pixels[i] - min) * scale);
                if (b < medBin && pixels[i] > lower)
                    lower = pixels[i];
            }
        }
        return (lower + upper) / 2.0;
    }

    private static void ValidateDimensions(FitsImageIO.ImageData a, FitsImageIO.ImageData b, string context)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException(
                $"Dimension mismatch: {a.Width}x{a.Height} vs {b.Width}x{b.Height} ({context})");
    }

    private static double ParseExposure(Dictionary<string, string> h)
    {
        foreach (var key in new[] { "EXPTIME", "EXPOSURE" })
        {
            if (h.TryGetValue(key, out var v) &&
                double.TryParse(v, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return 0;
    }

    private static string GuessFilter(List<string> paths, string dir)
    {
        foreach (var p in paths)
        {
            var name = Path.GetFileNameWithoutExtension(p);
            // Common patterns: _L_, _Ha_, _R_, _Filter-Ha_
            var m = FilterRegex.Match(name);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        var last = Path.GetFileName(dir);
        return string.IsNullOrEmpty(last) ? "UNKNOWN" : last.ToUpperInvariant();
    }

    private static string GuessDate(string dir)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dir, @"\b(20\d{2}-\d{2}-\d{2})\b");
        return m.Success ? m.Groups[1].Value : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(?:^|[_\-])(?:FILTER)?[_\-]?([LRGBSHO]a?|Ha|SII|OIII|NII)(?:[_\-]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "nb-NO")]
    private static partial Regex MyRegex();
}






