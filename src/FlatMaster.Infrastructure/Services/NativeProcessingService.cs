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
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Native (non-PixInsight) flat calibration and integration engine.
/// Performs: dark subtraction, Winsorized Sigma Clipping rejection, and average combination.
/// Output is Float32 XISF, matching the PixInsight pipeline's mathematical operations.
/// </summary>
public sealed class NativeProcessingService : IImageProcessingEngine
{
    private readonly ILogger<NativeProcessingService> _logger;
    private readonly IDarkMatchingService _darkMatcher;

    public NativeProcessingService(
        ILogger<NativeProcessingService> logger,
        IDarkMatchingService darkMatcher)
    {
        _logger = logger;
        _darkMatcher = darkMatcher;
    }

    public async Task<ProcessingResult> ExecuteAsync(
        ProcessingPlan plan,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        var io = new FitsImageIO(_logger);
        _logger.LogInformation("Native Processing Engine started");
        progress.Report("═══ Native Processing Engine ═══");

        if (plan.Jobs.Count == 0)
        {
            return new ProcessingResult
            {
                Success = false, ErrorMessage = "No jobs in plan",
                Output = "Validation failed: No jobs", ExitCode = 1
            };
        }

        int successCount = 0;
        int failureCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var job in plan.Jobs)
        {
            if (cancellationToken.IsCancellationRequested) break;
            progress.Report($"\n── Job: {job.DirectoryPath} ──");

            foreach (var group in job.ExposureGroups)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var darkMatch = _darkMatcher.FindBestDark(
                        group, plan.DarkCatalog, plan.Configuration.DarkMatching);

                    if (darkMatch == null)
                    {
                        progress.Report(string.Format(CultureInfo.InvariantCulture, "  ✗ No dark for {0:F3}s — skipped", group.ExposureTime));
                        failureCount++;
                        continue;
                    }

                    progress.Report($"  Dark: {Path.GetFileName(darkMatch.FilePath)} [{darkMatch.MatchKind}]");

                    await ProcessGroupAsync(
                        io, group, darkMatch, job, plan.Configuration,
                        progress, cancellationToken);

                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed group {Exp}s", group.ExposureTime);
                    progress.Report($"  ✗ ERROR: {ex.Message}");
                    failureCount++;
                }
            }
        }

        sw.Stop();
        var summary = string.Format(CultureInfo.InvariantCulture, "Done in {0:F1}s — {1} succeeded, {2} failed", sw.Elapsed.TotalSeconds, successCount, failureCount);
        progress.Report($"\n{summary}");

        return new ProcessingResult
        {
            Success = failureCount == 0,
            Output = summary,
            ExitCode = failureCount == 0 ? 0 : 1
        };
    }

    // ────────────────────── Per-group pipeline ──────────────────────

    private async Task ProcessGroupAsync(
        FitsImageIO io,
        ExposureGroup group,
        DarkMatchResult darkMatch,
        DirectoryJob job,
        ProcessingConfiguration config,
        IProgress<string> progress,
        CancellationToken ct)
    {
        // Sort file paths for deterministic processing order (matches PI's filesystem-order enumeration)
        var flatPaths = group.FilePaths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        int n = flatPaths.Count;
        progress.Report(string.Format(CultureInfo.InvariantCulture, "  Loading {0} flat frames ({1:F3}s)…", n, group.ExposureTime));

        // 1. Load dark master
        var darkImage = await io.ReadAsync(darkMatch.FilePath, ct);
        progress.Report($"  Dark loaded: {darkImage.Width}×{darkImage.Height}");

        if (darkMatch.OptimizeRequired)
        {
            // Scale the dark by the exposure ratio  (flatExp / darkExp)
            double darkExp = ParseExposure(darkImage.Headers);
            if (darkExp > 0 && group.ExposureTime > 0)
            {
                double scale = group.ExposureTime / darkExp;
                progress.Report(string.Format(CultureInfo.InvariantCulture, "  Optimizing dark: scale ×{0:F4} ({1:F3}s → {2:F3}s)", scale, darkExp, group.ExposureTime));
                ScalePixels(darkImage.Pixels, scale);
            }
        }

        // 2. Load and calibrate each flat (subtract dark)
        var calibrated = new FitsImageIO.ImageData[n];
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var flat = await io.ReadAsync(flatPaths[i], ct);
            ValidateDimensions(flat, darkImage, flatPaths[i]);
            SubtractDark(flat.Pixels, darkImage.Pixels);
            calibrated[i] = flat;
            if ((i + 1) % 5 == 0 || i == n - 1)
                progress.Report($"  Calibrated {i + 1}/{n}");
        }

        // 3. Normalise to multiplicative (divide each frame by its median — matches PI)
        progress.Report("Normalising (multiplicative, median-based)…");
        var medians = new double[n];
        for (int i = 0; i < n; i++)
        {
            medians[i] = ComputeMedian(calibrated[i].Pixels);
            if (Math.Abs(medians[i]) > 1e-15)
                DividePixels(calibrated[i].Pixels, medians[i]);
        }
        progress.Report(string.Format(CultureInfo.InvariantCulture,
            "  Frame medians: [{0}]", string.Join(", ", medians.Select(m => m.ToString("F6", CultureInfo.InvariantCulture)))));

        // 3b. Compute EqualizeFluxes rejection-normalization factors
        //     After multiplicative normalization, each frame's median ≈ 1.0.
        //     EqualizeFluxes additionally scales by refMean/frameMean so rejection
        //     testing uses equalized pixel values — matches PI's rejectionNormalization = EqualizeFluxes.
        var eqFactors = new double[n];
        double refNormMean = ComputeMean(calibrated[0].Pixels);
        for (int i = 0; i < n; i++)
        {
            double frameMean = ComputeMean(calibrated[i].Pixels);
            eqFactors[i] = (Math.Abs(frameMean) > 1e-15) ? refNormMean / frameMean : 1.0;
        }

        // 4. Integrate
        var rej = config.Rejection;
        progress.Report(string.Format(CultureInfo.InvariantCulture, "  Integrating: Winsorized Sigma Clip (σ_low={0:F1}, σ_high={1:F1})…", rej.LowSigma, rej.HighSigma));

        long pixelCount = (long)calibrated[0].Width * calibrated[0].Height * calibrated[0].Channels;
        var result = new double[pixelCount];

        // Select rejection strategy based on frame count (mirrors the PJSR template)
        if (n < 3)
        {
            // Straight average, no rejection possible
            AverageStack(calibrated, result, pixelCount);
        }
        else if (n < 6)
        {
            // Percentile clip for small stacks
            progress.Report("  (Small stack: using Percentile Clip)");
            PercentileClipStack(calibrated, result, pixelCount, 0.20, 0.10, eqFactors);
        }
        else
        {
            // Winsorized Sigma Clipping for >= 6 frames
            WinsorizedSigmaClipStack(calibrated, result, pixelCount,
                rej.LowSigma, rej.HighSigma, winsorizationCutoff: 5.0, maxIterations: 10, eqFactors);
        }

        // 5. Rescale result by reference median (first frame) — matches PI multiplicative normalization
        double referenceMedian = medians[0];
        if (Math.Abs(referenceMedian) > 1e-15)
        {
            for (long p = 0; p < pixelCount; p++)
                result[p] *= referenceMedian;
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
        var masterName = string.Format(CultureInfo.InvariantCulture, "MasterFlat_{0}_{1}_Bin{2}_{3:F3}s.xisf", date, filter, binning, group.ExposureTime);

        var outDir = job.OutputRootPath;
        if (!string.IsNullOrEmpty(job.RelativeDirectory) && job.RelativeDirectory != ".")
            outDir = Path.Combine(outDir, job.RelativeDirectory);
        var masterPath = Path.Combine(outDir, masterName);

        progress.Report($"  Writing: {masterPath}");
        await io.WriteXisfAsync(masterPath, master, ct);
        progress.Report($"  ✓ Master flat written ({master.Width}×{master.Height}, {n} frames integrated)");
    }

    // ════════════════════ Integration Algorithms ════════════════════

    /// <summary>
    /// Winsorized Sigma Clipping: iterative rejection where outliers are replaced
    /// by the clipping boundary value before recomputing statistics. This prevents
    /// outliers from inflating sigma, giving more aggressive rejection.
    /// Matches PixInsight's ImageIntegration WSC implementation.
    /// When eqFactors is provided (EqualizeFluxes), rejection testing uses
    /// equalized values but the final average uses original normalized values.
    /// </summary>
    private static void WinsorizedSigmaClipStack(
        FitsImageIO.ImageData[] frames, double[] output, long pixelCount,
        double sigmaLow, double sigmaHigh, double winsorizationCutoff, int maxIterations,
        double[]? eqFactors = null)
    {
        int n = frames.Length;
        var eqCol = new double[n];       // equalized values — used for rejection testing
        var origCol = new double[n];     // original normalized values — used for final average
        var included = new bool[n];
        var winsorized = new double[n];
        bool hasEq = eqFactors != null;

        for (long p = 0; p < pixelCount; p++)
        {
            // Collect the pixel column across all frames
            for (int i = 0; i < n; i++)
            {
                double v = frames[i].Pixels[p];
                origCol[i] = v;
                eqCol[i] = hasEq ? v * eqFactors![i] : v;
            }

            // Start with all included
            for (int i = 0; i < n; i++) included[i] = true;
            int count = n;

            for (int iter = 0; iter < maxIterations && count >= 3; iter++)
            {
                // Compute mean and sigma of included equalized pixels
                double sum = 0;
                for (int i = 0; i < n; i++)
                    if (included[i]) sum += eqCol[i];
                double mean = sum / count;

                // Winsorize: replace values beyond cutoff*sigma with boundary
                // First compute raw sigma
                double ssq = 0;
                for (int i = 0; i < n; i++)
                {
                    if (included[i])
                    {
                        double d = eqCol[i] - mean;
                        ssq += d * d;
                    }
                }
                double sigma = Math.Sqrt(ssq / (count - 1));
                if (sigma < 1e-15) break; // already converged

                // Build winsorized copy for more robust sigma
                double loClip = mean - winsorizationCutoff * sigma;
                double hiClip = mean + winsorizationCutoff * sigma;
                for (int i = 0; i < n; i++)
                {
                    if (!included[i]) continue;
                    winsorized[i] = Math.Clamp(eqCol[i], loClip, hiClip);
                }

                // Recompute sigma from winsorized values
                double wSum = 0;
                for (int i = 0; i < n; i++)
                    if (included[i]) wSum += winsorized[i];
                double wMean = wSum / count;

                double wSsq = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!included[i]) continue;
                    double d = winsorized[i] - wMean;
                    wSsq += d * d;
                }
                double wSigma = Math.Sqrt(wSsq / (count - 1));
                if (wSigma < 1e-15) break;

                // Reject equalized pixels outside sigma bounds (using winsorized sigma)
                double rejLo = mean - sigmaLow * wSigma;
                double rejHi = mean + sigmaHigh * wSigma;
                bool anyRejected = false;

                for (int i = 0; i < n; i++)
                {
                    if (!included[i]) continue;
                    if (eqCol[i] < rejLo || eqCol[i] > rejHi)
                    {
                        included[i] = false;
                        count--;
                        anyRejected = true;
                    }
                }

                if (!anyRejected) break;
            }

            // Average surviving ORIGINAL normalized pixels (not equalized)
            if (count > 0)
            {
                double sum = 0;
                for (int i = 0; i < n; i++)
                    if (included[i]) sum += origCol[i];
                output[p] = sum / count;
            }
            else
            {
                // All rejected — fallback to median of originals
                Array.Sort(origCol);
                output[p] = origCol[n / 2];
            }
        }
    }

    /// <summary>
    /// Percentile clip: reject the bottom and top percentiles, average the rest.
    /// Used for small stacks (n &lt; 6) where sigma clipping is unreliable.
    /// When eqFactors is provided (EqualizeFluxes), sorting uses equalized values
    /// but averaging uses original normalized values.
    /// </summary>
    private static void PercentileClipStack(
        FitsImageIO.ImageData[] frames, double[] output, long pixelCount,
        double lowClip, double highClip, double[]? eqFactors = null)
    {
        int n = frames.Length;
        var column = new double[n];
        var indices = new int[n];

        for (long p = 0; p < pixelCount; p++)
        {
            for (int i = 0; i < n; i++)
            {
                column[i] = (eqFactors != null)
                    ? frames[i].Pixels[p] * eqFactors[i]
                    : frames[i].Pixels[p];
                indices[i] = i;
            }

            // Sort indices by (equalized) column values
            Array.Sort(column, indices);

            int lo = (int)Math.Floor(n * lowClip);
            int hi = n - (int)Math.Floor(n * highClip);
            if (hi <= lo) { lo = 0; hi = n; } // safety

            // Average original normalized values for the kept range
            double sum = 0;
            for (int i = lo; i < hi; i++) sum += frames[indices[i]].Pixels[p];
            output[p] = sum / (hi - lo);
        }
    }

    /// <summary>
    /// Simple average (no rejection). Used when n &lt; 3.
    /// </summary>
    private static void AverageStack(
        FitsImageIO.ImageData[] frames, double[] output, long pixelCount)
    {
        int n = frames.Length;
        for (long p = 0; p < pixelCount; p++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += frames[i].Pixels[p];
            output[p] = sum / n;
        }
    }

    // ════════════════════ Pixel Math Helpers ════════════════════

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

    /// <summary>
    /// Computes the exact median using a 3-pass histogram refinement approach.
    /// Pass 1: find min/max range.  Pass 2: 1M-bin histogram to locate the median bin.
    /// Pass 3: collect and sort only the values in that bin for a precise result.
    /// O(n) time, ~4 MB histogram — no large array copy needed.
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

        // Pass 2: histogram — locate the bin containing the median
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
                $"Dimension mismatch: {a.Width}×{a.Height} vs {b.Width}×{b.Height} ({context})");
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
            var m = System.Text.RegularExpressions.Regex.Match(name,
                @"(?:^|[_\-])(?:FILTER)?[_\-]?([LRGBSHO]a?|Ha|SII|OIII|NII)(?:[_\-]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
}

