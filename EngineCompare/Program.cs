using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FlatMaster.Core.Configuration;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;

// ── Configuration ──
var FlatsRoot = Environment.GetEnvironmentVariable("FM_FLATS_ROOT") ?? @"C:\Users\riise\Pictures\RC16";
var DarksRoot = Environment.GetEnvironmentVariable("FM_DARKS_ROOT") ?? @"C:\Users\riise\Pictures\RC Darks";
var PIExe = Environment.GetEnvironmentVariable("FM_PI_EXE") ?? @"C:\Program Files\PixInsight\bin\PixInsight.exe";
var SkipNative = string.Equals(Environment.GetEnvironmentVariable("FM_SKIP_NATIVE"), "1", StringComparison.OrdinalIgnoreCase);
var SingleTest = string.Equals(Environment.GetEnvironmentVariable("FM_SINGLE_TEST"), "1", StringComparison.OrdinalIgnoreCase);
var tmpBase = Path.Combine(Path.GetTempPath(), "FlatMaster_Compare");

// ── Set up services (manual DI) ──
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
var metaLogger = loggerFactory.CreateLogger<MetadataReaderService>();
var scanLogger = loggerFactory.CreateLogger<FileScannerService>();
var darkMatchLogger = loggerFactory.CreateLogger<DarkMatchingService>();
var nativeLogger = loggerFactory.CreateLogger<NativeProcessingService>();
var piLogger = loggerFactory.CreateLogger<PixInsightService>();
var ioLogger = loggerFactory.CreateLogger<FitsImageIO>();

var cache = new MemoryCache(new MemoryCacheOptions());
var metaOpts = Options.Create(new MetadataReaderOptions());
var metaReader = new MetadataReaderService(metaLogger, cache, metaOpts);
var fileScanner = new FileScannerService(metaReader, scanLogger);
var darkMatcher = new DarkMatchingService(darkMatchLogger);
var nativeEngine = new NativeProcessingService(nativeLogger, darkMatcher);
var piEngine = new PixInsightService(piLogger);
var fitsIO = new FitsImageIO(ioLogger);

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║   FlatMaster Engine Comparison: Native vs PixInsight    ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

// ── Step 1: Scan flats ──
Console.Write("Scanning flats...");
var scanProgress = new Progress<ScanProgress>(p =>
    Console.Write($"\r  Scanning flats... {p.DirectoriesScanned} dirs, {p.FitsFound + p.XisfFound} files  "));
var flatJobs = await fileScanner.ScanFlatDirectoriesAsync(new[] { FlatsRoot }, scanProgress);
Console.WriteLine($"\r  Found {flatJobs.Count} directories with flat groups.                    ");

// ── Step 2: Scan darks ──
Console.Write("Scanning darks...");
var darkProgress = new Progress<ScanProgress>(p =>
    Console.Write($"\r  Scanning darks... {p.DirectoriesScanned} dirs, {p.FitsFound + p.XisfFound} files  "));
var darks = await fileScanner.ScanDarkLibraryAsync(new[] { DarksRoot }, darkProgress);
Console.WriteLine($"\r  Found {darks.Count} dark frames.                                        ");

if (flatJobs.Count == 0)
{
    Console.WriteLine("ERROR: No flats found. Aborting.");
    return;
}

// ── Step 3: Pick test groups — smallest (3 frames) and a medium one (5-20) ──
var candidates = new List<(DirectoryJob job, ExposureGroup group)>();
foreach (var job in flatJobs)
{
    // Skip directories flagged for deletion — they often contain mixed/experimental data
    if (job.DirectoryPath.Contains("deleteme", StringComparison.OrdinalIgnoreCase))
        continue;
    foreach (var g in job.ExposureGroups)
        if (g.FilePaths.Count >= 3)
            candidates.Add((job, g));
}

if (candidates.Count == 0)
{
    Console.WriteLine("ERROR: No exposure group with ≥3 frames found.");
    return;
}

candidates.Sort((a, b) => a.group.FilePaths.Count.CompareTo(b.group.FilePaths.Count));

// Pick: smallest, and one with 10-25 frames (or next largest if none that big)
var testGroups = new List<(DirectoryJob job, ExposureGroup group, string label)>();
testGroups.Add((candidates[0].job, candidates[0].group, "Small stack"));

if (!SingleTest)
{
    var medium = candidates.FirstOrDefault(c => c.group.FilePaths.Count >= 10 && c.group.FilePaths.Count <= 25);
    if (medium.job != null)
        testGroups.Add((medium.job, medium.group, "Medium stack"));
    else
    {
        var larger = candidates.FirstOrDefault(c => c.group.FilePaths.Count >= 6);
        if (larger.job != null && larger.group != candidates[0].group)
            testGroups.Add((larger.job, larger.group, "Larger stack"));
    }
}

var darkMatchOpts = new DarkMatchingOptions
{
    EnforceBinning = true,
    PreferSameGainOffset = true,
    PreferClosestTemp = true,
    MaxTempDeltaC = 5.0,
    AllowNearestExposureWithOptimize = true
};

foreach (var (testJob, testGroup, label) in testGroups)
{
    Console.WriteLine();
    Console.WriteLine($"╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  TEST: {label} — {testGroup.FilePaths.Count} frames × {testGroup.ExposureTime:F3}s ({testGroup.RepresentativeMetadata?.Filter ?? "?"})");
    Console.WriteLine($"║  Dir:  {testJob.DirectoryPath}");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════╝");

    var darkMatch = darkMatcher.FindBestDark(testGroup, darks, darkMatchOpts);
    if (darkMatch != null)
        Console.WriteLine($"  Dark: {Path.GetFileName(darkMatch.FilePath)} [{darkMatch.MatchKind}]");
    else
        Console.WriteLine("  WARNING: No matching dark found!");

    await RunComparison(testJob, testGroup, darks, darkMatchOpts, label);
}

Console.WriteLine("\n  All tests complete.");
return;

// ═══════════════════════════════════════════════════════════════════
async Task RunComparison(DirectoryJob bestJob, ExposureGroup bestGroup,
    List<DarkFrame> darkCatalog, DarkMatchingOptions matchOpts, string label)
{
    var runDir = Path.Combine(tmpBase, label.Replace(" ", "_"));
    var nativeOut = Path.Combine(runDir, "Native");
    var piOut = Path.Combine(runDir, "PixInsight");
    if (Directory.Exists(runDir)) Directory.Delete(runDir, true);
    Directory.CreateDirectory(nativeOut);
    Directory.CreateDirectory(piOut);

    var config = new ProcessingConfiguration
    {
        PixInsightExecutable = PIExe,
        DeleteCalibratedFlats = false,
        CacheDirName = "_DarkMasters",
        CalibratedSubdirBase = "_CalibratedFlats",
        XisfHintsCal = "",
        XisfHintsMaster = "",
        Rejection = new RejectionSettings { LowSigma = 5.0, HighSigma = 5.0 },
        DarkMatching = matchOpts,
        RequireDarks = false
    };

    var nativeJob = new DirectoryJob
    {
        DirectoryPath = bestJob.DirectoryPath,
        BaseRootPath = bestJob.BaseRootPath,
        OutputRootPath = nativeOut,
        RelativeDirectory = ".",
        ExposureGroups = new List<ExposureGroup> { bestGroup }
    };
    var nativePlan = new ProcessingPlan
    {
        Jobs = new List<DirectoryJob> { nativeJob },
        DarkCatalog = darkCatalog,
        Configuration = config
    };

    var piJob = new DirectoryJob
    {
        DirectoryPath = bestJob.DirectoryPath,
        BaseRootPath = bestJob.BaseRootPath,
        OutputRootPath = piOut,
        RelativeDirectory = ".",
        ExposureGroups = new List<ExposureGroup> { bestGroup }
    };
    var piPlan = new ProcessingPlan
    {
        Jobs = new List<DirectoryJob> { piJob },
        DarkCatalog = darkCatalog,
        Configuration = config
    };

    var logProgress = new Progress<string>(msg => Console.WriteLine($"    {msg}"));

// ── Step 5: Run Native Engine ──
    Console.WriteLine();
    var sw = Stopwatch.StartNew();
    ProcessingResult nativeResult;
    long nativeMs;

    if (!SkipNative)
    {
        Console.WriteLine("  ── Running Native Engine ──");
        nativeResult = await nativeEngine.ExecuteAsync(nativePlan, logProgress, CancellationToken.None);
        nativeMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"  Native: {(nativeResult.Success ? "SUCCESS" : "FAILED")} in {nativeMs:N0}ms");
    }
    else
    {
        nativeResult = new ProcessingResult { Success = true, ExitCode = 0, Output = "Skipped native engine." };
        nativeMs = 0;
        Console.WriteLine("  ── Running Native Engine ──");
        Console.WriteLine("  Native: SKIPPED");
    }

    // ── Step 6: Run PixInsight Engine ──
    Console.WriteLine();
    Console.WriteLine("  ── Running PixInsight Engine ──");
    sw.Restart();
    var piResult = await piEngine.ProcessJobsInBatchesAsync(
        piPlan, PIExe, 25, logProgress, CancellationToken.None);
    var piMs = sw.ElapsedMilliseconds;
    Console.WriteLine($"  PixInsight: {(piResult.Success ? "SUCCESS" : "FAILED")} in {piMs:N0}ms");

    if (!nativeResult.Success || !piResult.Success)
    {
        Console.WriteLine("  ERROR: One or both engines failed.");
        if (!nativeResult.Success) Console.WriteLine($"    Native: {nativeResult.ErrorMessage}");
        if (!piResult.Success) Console.WriteLine($"    PI: {piResult.ErrorMessage}");
        return;
    }

    if (SkipNative)
    {
        Console.WriteLine("  PixInsight smoke test passed (native comparison skipped).");
        return;
    }

    // ── Step 7: Find output master flats ──
    var nativeFiles = Directory.GetFiles(nativeOut, "MasterFlat_*.xisf", SearchOption.AllDirectories);
    var piFiles = Directory.GetFiles(piOut, "MasterFlat_*.xisf", SearchOption.AllDirectories);
    if (nativeFiles.Length == 0 || piFiles.Length == 0)
    {
        var allN = Directory.GetFiles(nativeOut, "*.xisf", SearchOption.AllDirectories);
        var allP = Directory.GetFiles(piOut, "*.xisf", SearchOption.AllDirectories);
        Console.WriteLine($"  MasterFlat: Native={nativeFiles.Length}, PI={piFiles.Length}. All XISF: N={allN.Length}, PI={allP.Length}");
        foreach (var f in allN) Console.WriteLine($"    N: {f}");
        foreach (var f in allP) Console.WriteLine($"    P: {f}");
        return;
    }

    // ── Step 8: Pixel-level comparison ──
    var nativeImg = await fitsIO.ReadAsync(nativeFiles[0]);
    var piImg = await fitsIO.ReadAsync(piFiles[0]);
    Console.WriteLine($"\n  Native: {nativeImg.Width}x{nativeImg.Height}  PI: {piImg.Width}x{piImg.Height}");

    if (nativeImg.Width != piImg.Width || nativeImg.Height != piImg.Height) { Console.WriteLine("  ERROR: Dimension mismatch!"); return; }

    long pxCount = (long)nativeImg.Width * nativeImg.Height * nativeImg.Channels;
    double sumAbsDiff = 0, maxAbsDiff = 0, sumSqDiff = 0;
    double sumNative = 0, sumPI = 0;
    double minN = double.MaxValue, maxN = double.MinValue;
    double minP = double.MaxValue, maxP = double.MinValue;
    long countOver01Pct = 0, countOver1Pct = 0, countOver5Pct = 0;

    for (long i = 0; i < pxCount; i++)
    {
        double n = nativeImg.Pixels[i]; double p = piImg.Pixels[i];
        sumNative += n; sumPI += p;
        if (n < minN) minN = n; if (n > maxN) maxN = n;
        if (p < minP) minP = p; if (p > maxP) maxP = p;
        double diff = Math.Abs(n - p);
        sumAbsDiff += diff;
        if (diff > maxAbsDiff) maxAbsDiff = diff;
        sumSqDiff += diff * diff;
        double denom = Math.Max(Math.Abs(p), 1e-10);
        double rel = diff / denom;
        if (rel > 0.001) countOver01Pct++;
        if (rel > 0.01) countOver1Pct++;
        if (rel > 0.05) countOver5Pct++;
    }

    double meanAbsDiff = sumAbsDiff / pxCount;
    double rms = Math.Sqrt(sumSqDiff / pxCount);
    double meanN = sumNative / pxCount;
    double meanP = sumPI / pxCount;

    double covSum = 0, varN = 0, varP = 0;
    for (long i = 0; i < pxCount; i++)
    {
        double dn = nativeImg.Pixels[i] - meanN;
        double dp = piImg.Pixels[i] - meanP;
        covSum += dn * dp; varN += dn * dn; varP += dp * dp;
    }
    double correlation = covSum / (Math.Sqrt(varN) * Math.Sqrt(varP) + 1e-20);

    // Sampled percentiles
    var rng = new Random(42);
    int sampleSz = Math.Min(2_000_000, (int)pxCount);
    var sN = new double[sampleSz]; var sP = new double[sampleSz]; var sD = new double[sampleSz];
    for (int i = 0; i < sampleSz; i++)
    {
        int idx = (sampleSz == (int)pxCount) ? i : rng.Next((int)pxCount);
        sN[i] = nativeImg.Pixels[idx]; sP[i] = piImg.Pixels[idx]; sD[i] = nativeImg.Pixels[idx] - piImg.Pixels[idx];
    }
    Array.Sort(sN); Array.Sort(sP); Array.Sort(sD);

    double Pct(double[] s, double p) { double x = p * (s.Length - 1); int l = (int)Math.Floor(x); int h = (int)Math.Ceiling(x); return l == h ? s[l] : s[l] * (1 - (x - l)) + s[h] * (x - l); }

    double medN = Pct(sN, 0.5), medP = Pct(sP, 0.5);

    // ── Print results ──
    Console.WriteLine();
    Console.WriteLine("  ╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine($"  ║  {label}: {bestGroup.FilePaths.Count} flats × {bestGroup.ExposureTime:F3}s ({bestGroup.RepresentativeMetadata?.Filter ?? "?"})");
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Image: {0}×{1} ({2:N0} pixels)", nativeImg.Width, nativeImg.Height, pxCount));
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Speed: Native {0:N0}ms vs PI {1:N0}ms ({2:F1}× ratio)", nativeMs, piMs, (double)piMs / Math.Max(1, nativeMs)));
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Native   mean={0:E6}  median={1:E6}", meanN, medN));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  PI       mean={0:E6}  median={1:E6}", meanP, medP));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Mean Δ offset: {0:+0.0000%;-0.0000%}", (meanN - meanP) / Math.Max(Math.Abs(meanP), 1e-15)));
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Mean |Δ|:        {0:E6}  ({1:F4}% of PI mean)", meanAbsDiff, 100.0 * meanAbsDiff / Math.Max(meanP, 1e-15)));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Max  |Δ|:        {0:E6}", maxAbsDiff));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  RMS:             {0:E6}", rms));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Pearson r:       {0:F10}", correlation));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Pixels > 0.1%:   {0:N0} ({1:F4}%)", countOver01Pct, 100.0 * countOver01Pct / pxCount));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Pixels > 1%:     {0:N0} ({1:F4}%)", countOver1Pct, 100.0 * countOver1Pct / pxCount));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║  Pixels > 5%:     {0:N0} ({1:F4}%)", countOver5Pct, 100.0 * countOver5Pct / pxCount));
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");
    Console.WriteLine("  ║  Percentile values:                                     ║");
    foreach (var pct in new[] { 0.01, 0.05, 0.25, 0.50, 0.75, 0.95, 0.99 })
    {
        double pN2 = Pct(sN, pct), pP2 = Pct(sP, pct);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  ║    P{0,5:F1}%:  N={1:F6}  PI={2:F6}  Δ={3:+0.000%;-0.000%;0.000%}", pct * 100, pN2, pP2, (pN2 - pP2) / Math.Max(Math.Abs(pP2), 1e-15)));
    }
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");
    Console.WriteLine("  ║  Signed diff (Native − PI):                             ║");
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║    P1={0:+0.0000E+00;-0.0000E+00}  P25={1:+0.0000E+00;-0.0000E+00}  P50={2:+0.0000E+00;-0.0000E+00}",
        Pct(sD, 0.01), Pct(sD, 0.25), Pct(sD, 0.50)));
    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
        "  ║    P75={0:+0.0000E+00;-0.0000E+00}  P99={1:+0.0000E+00;-0.0000E+00}",
        Pct(sD, 0.75), Pct(sD, 0.99)));
    Console.WriteLine("  ╠══════════════════════════════════════════════════════════╣");

    string verdict;
    if (correlation > 0.9999 && meanAbsDiff / Math.Max(meanP, 1e-10) < 0.001)
        verdict = "EXCELLENT — virtually identical";
    else if (correlation > 0.999 && meanAbsDiff / Math.Max(meanP, 1e-10) < 0.01)
        verdict = "VERY GOOD — sub-percent differences";
    else if (correlation > 0.99)
        verdict = "GOOD — small systematic differences";
    else
        verdict = "SIGNIFICANT DIFFERENCES";

    Console.WriteLine($"  ║  VERDICT: {verdict}");
    Console.WriteLine("  ╚══════════════════════════════════════════════════════════╝");
}
