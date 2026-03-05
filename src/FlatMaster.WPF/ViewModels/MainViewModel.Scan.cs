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

using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task ScanAllAsync()
    {
        if (DarksOnlyProcessMode)
        {
            if (DarkLibraryRoots.Count == 0)
            {
                WpfMessageBox.Show("Please add at least one dark library directory.", "No Directories",
                    WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
                return;
            }
        }
        else
        {
            if (FlatBaseRoots.Count == 0 || DarkLibraryRoots.Count == 0)
            {
                var missing = new List<string>();
                if (FlatBaseRoots.Count == 0) missing.Add("flat base directory");
                if (DarkLibraryRoots.Count == 0) missing.Add("dark library directory");

                WpfMessageBox.Show(
                    $"Please add at least one {string.Join(" and one ", missing)}.",
                    "No Directories",
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Information);
                return;
            }
        }

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        IsScanning = true;
        IsCancellable = true;
        IsFlatScanActive = false;
        IsDarkScanActive = false;
        FlatScanProgressValue = 0;
        DarkScanProgressValue = 0;

        try
        {
            InvalidateMatchingResultsAfterRescan();

            if (DarksOnlyProcessMode)
            {
                StatusMessage = "Scanning dark library...";
                await ScanDarksCoreAsync(cancellationToken);
                UpdateScanSummaryStatusMessage();
                return;
            }

            StatusMessage = "Benchmarking scan strategy...";
            var parallel = await ShouldUseParallelScanAsync(cancellationToken);
            if (parallel)
            {
                StatusMessage = "Scanning flats and darks in parallel...";
                Log("[Scan] Scanning flats and darks in parallel (adaptive benchmark).");
                await Task.WhenAll(
                    ScanFlatsCoreAsync(cancellationToken),
                    ScanDarksCoreAsync(cancellationToken));
            }
            else
            {
                StatusMessage = "Scanning flats and darks...";
                Log("[Scan] Scanning flats and darks sequentially (adaptive benchmark fallback).");
                await ScanFlatsCoreAsync(cancellationToken);
                await ScanDarksCoreAsync(cancellationToken);
            }

            UpdateScanSummaryStatusMessage();
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled by user");
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running combined scan");
            Log($"ERROR: {ex.Message}");
            WpfMessageBox.Show($"Error scanning: {ex.Message}", "Error",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            IsCancellable = false;
        }
    }

    [RelayCommand]
    private async Task ScanFlatsAsync()
    {
        if (FlatBaseRoots.Count == 0)
        {
            WpfMessageBox.Show("Please add at least one flat base directory.", "No Directories",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        IsScanning = true;
        IsCancellable = true;
        IsFlatScanActive = false;
        IsDarkScanActive = false;
        FlatScanProgressValue = 0;
        DarkScanProgressValue = 0;

        try
        {
            InvalidateMatchingResultsAfterRescan();
            await ScanFlatsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Log("Flat scan cancelled by user");
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning flats");
            Log($"ERROR: {ex.Message}");
            WpfMessageBox.Show($"Error scanning flats: {ex.Message}", "Error",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            IsCancellable = false;
            IsFlatScanActive = false;
            IsDarkScanActive = false;
        }
    }

    [RelayCommand]
    private async Task ScanDarksAsync()
    {
        if (DarkLibraryRoots.Count == 0)
        {
            WpfMessageBox.Show("Please add at least one dark library directory.", "No Directories",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        IsScanning = true;
        IsCancellable = true;
        IsFlatScanActive = false;
        IsDarkScanActive = false;
        FlatScanProgressValue = 0;
        DarkScanProgressValue = 0;

        try
        {
            InvalidateMatchingResultsAfterRescan();
            await ScanDarksCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Log("Dark scan cancelled by user");
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning darks");
            Log($"ERROR: {ex.Message}");
            WpfMessageBox.Show($"Error scanning darks: {ex.Message}", "Error",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            IsCancellable = false;
            IsFlatScanActive = false;
            IsDarkScanActive = false;
        }
    }

    private sealed record ScanProbeResult(
        int FlatFilesScanned,
        int DarkFilesScanned,
        int FlatDirsScanned,
        int DarkDirsScanned,
        double DurationSeconds);

    private sealed record ProbeCounters(int FilesScanned, int DirectoriesScanned, double DurationSeconds);

    private static readonly HashSet<string> ProbeImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fits",
        ".fit",
        ".xisf"
    };

    private static readonly HashSet<string> ProbeSkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "_darkmasters",
        "_calibratedflats",
        "masters",
        "_processed"
    };

    private async Task<bool> ShouldUseParallelScanAsync(CancellationToken cancellationToken)
    {
        var probeWindow = TimeSpan.FromSeconds(3);
        Log("[Scan] Running adaptive scan benchmark (3.0 seconds sequential + 3.0 seconds dual-thread)...");

        var singleProbe = await ProbeSequentialScanAsync(probeWindow, cancellationToken);
        var dualProbe = await ProbeParallelScanAsync(probeWindow, cancellationToken);

        var singleRate = singleProbe.DurationSeconds > 0
            ? (singleProbe.FlatFilesScanned + singleProbe.DarkFilesScanned) / singleProbe.DurationSeconds
            : 0;
        var dualTotalFiles = dualProbe.FlatFilesScanned + dualProbe.DarkFilesScanned;
        var dualRate = dualProbe.DurationSeconds > 0
            ? dualTotalFiles / dualProbe.DurationSeconds
            : 0;

        Log(string.Format(
            CultureInfo.InvariantCulture,
            "[Scan] Benchmark sequential: flats {0} files ({1} directories) + darks {2} files ({3} directories) in {4:0.00} seconds -> {5:0.0} files/second",
            singleProbe.FlatFilesScanned,
            singleProbe.FlatDirsScanned,
            singleProbe.DarkFilesScanned,
            singleProbe.DarkDirsScanned,
            singleProbe.DurationSeconds,
            singleRate));
        Log(string.Format(
            CultureInfo.InvariantCulture,
            "[Scan] Benchmark dual-thread: flats {0} files ({1} directories) + darks {2} files ({3} directories) in {4:0.00} seconds -> {5:0.0} files/second",
            dualProbe.FlatFilesScanned,
            dualProbe.FlatDirsScanned,
            dualProbe.DarkFilesScanned,
            dualProbe.DarkDirsScanned,
            dualProbe.DurationSeconds,
            dualRate));

        // Require a small margin to avoid flapping due to probe noise.
        var useParallel = dualRate > (singleRate * 1.05);
        Log(useParallel
            ? "[Scan] Adaptive decision: dual-thread scan selected."
            : "[Scan] Adaptive decision: sequential scan selected.");

        return useParallel;
    }

    private async Task<ScanProbeResult> ProbeSequentialScanAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var half = TimeSpan.FromMilliseconds(Math.Max(250, duration.TotalMilliseconds / 2.0));
        var flatCounters = await ProbeRootsForDurationAsync(FlatBaseRoots, half, cancellationToken);
        var darkCounters = await ProbeRootsForDurationAsync(DarkLibraryRoots, half, cancellationToken);

        return new ScanProbeResult(
            FlatFilesScanned: flatCounters.FilesScanned,
            DarkFilesScanned: darkCounters.FilesScanned,
            FlatDirsScanned: flatCounters.DirectoriesScanned,
            DarkDirsScanned: darkCounters.DirectoriesScanned,
            DurationSeconds: flatCounters.DurationSeconds + darkCounters.DurationSeconds);
    }

    private async Task<ScanProbeResult> ProbeParallelScanAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var flatTask = ProbeRootsForDurationAsync(FlatBaseRoots, duration, cancellationToken);
        var darkTask = ProbeRootsForDurationAsync(DarkLibraryRoots, duration, cancellationToken);
        await Task.WhenAll(flatTask, darkTask);

        var flat = await flatTask;
        var dark = await darkTask;

        return new ScanProbeResult(
            FlatFilesScanned: flat.FilesScanned,
            DarkFilesScanned: dark.FilesScanned,
            FlatDirsScanned: flat.DirectoriesScanned,
            DarkDirsScanned: dark.DirectoriesScanned,
            DurationSeconds: Math.Max(flat.DurationSeconds, dark.DurationSeconds));
    }

    private static async Task<ProbeCounters> ProbeRootsForDurationAsync(
        IEnumerable<string> roots,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(duration);
            var token = probeCts.Token;

            var stopwatch = Stopwatch.StartNew();
            var filesScanned = 0;
            var dirsScanned = 0;
            var queue = new Queue<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    queue.Enqueue(root);
            }

            while (queue.Count > 0 && !token.IsCancellationRequested)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current))
                    continue;

                dirsScanned++;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        if (token.IsCancellationRequested)
                            break;

                        if (ProbeImageExtensions.Contains(Path.GetExtension(file)))
                            filesScanned++;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore probe access errors.
                }
                catch (DirectoryNotFoundException)
                {
                    // Ignore probe access errors.
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(current))
                    {
                        if (token.IsCancellationRequested)
                            break;

                        var name = Path.GetFileName(subDir);
                        if (string.IsNullOrWhiteSpace(name) || ProbeSkipDirectories.Contains(name))
                            continue;

                        queue.Enqueue(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore probe access errors.
                }
                catch (DirectoryNotFoundException)
                {
                    // Ignore probe access errors.
                }
            }

            stopwatch.Stop();
            return new ProbeCounters(
                FilesScanned: filesScanned,
                DirectoriesScanned: dirsScanned,
                DurationSeconds: Math.Max(0.001, stopwatch.Elapsed.TotalSeconds));
        }, cancellationToken);
    }

    private async Task ScanFlatsCoreAsync(CancellationToken cancellationToken)
    {
        StatusMessage = "Scanning flat directories...";
        IsFlatScanActive = true;
        FlatScanProgressValue = 0;
        FlatScanProgressText = "Flat scan: starting...";
        UnregisterFlatDirectorySelectionTracking();
        FlatDirectories.Clear();

        var totalDirs = 0;
        var totalFiles = 0;
        var totalFits = 0;
        var totalXisf = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            totalDirs = p.DirectoriesScanned;
            totalFiles = p.FilesFound;
            totalFits = p.FitsFound;
            totalXisf = p.XisfFound;
            FlatScanProgressText = $"Flat scan: {p.DirectoriesScanned} directories, {p.FilesFound} files";
        });

        _flatJobs = await _fileScanner.ScanFlatDirectoriesAsync(FlatBaseRoots, progress, cancellationToken);

        foreach (var job in _flatJobs)
            FlatDirectories.Add(new DirectoryJobViewModel(job));
        RegisterFlatDirectorySelectionTracking();

        Log($"Flat scan complete: {_flatJobs.Count} directories with valid exposure groups");
        var totalFlats = _flatJobs.Sum(job => job.ExposureGroups.Sum(g => g.FilePaths.Count));
        Log($"  Scanned {totalDirs} directories, found {totalFiles} image files (FITS={totalFits}, XISF={totalXisf}), {totalFlats} flats suitable");

        if (_flatJobs.Count == 0 && totalFiles > 0)
        {
            Log("  WARNING: Found files but no valid flat groups. Check file log at:");
            Log($"  {_sessionLogPath}");
            Log("  Common issues: wrong ImageType (Dark/Bias instead of Flat), missing exposure, <3 files per group");
        }

        UpdateScanSummaryStatusMessage();
        IsFlatScanActive = false;
        FlatScanProgressValue = 1;
        FlatScanProgressText = $"Flat scan: done ({totalDirs} directories, {totalFiles} files)";
    }

    private async Task ScanDarksCoreAsync(CancellationToken cancellationToken)
    {
        StatusMessage = "Scanning dark library...";
        IsDarkScanActive = true;
        DarkScanProgressValue = 0;
        DarkScanProgressText = "Dark scan: starting...";
        UnregisterDarkSelectionTracking();
        DarkInventory.Clear();

        var totalDirs = 0;
        var totalFiles = 0;
        var totalFits = 0;
        var totalXisf = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            totalDirs = p.DirectoriesScanned;
            totalFiles = p.FilesFound;
            totalFits = p.FitsFound;
            totalXisf = p.XisfFound;
            DarkScanProgressText = $"Dark scan: {p.DirectoriesScanned} directories, {p.FilesFound} files";
        });

        _darkCatalog = await _fileScanner.ScanDarkLibraryAsync(DarkLibraryRoots, progress, cancellationToken);

        var grouped = _darkCatalog
            .GroupBy(d => d.Type)
            .OrderBy(g => g.Key);

        foreach (var typeGroup in grouped)
        {
            var typeVm = new DarkGroupViewModel(typeGroup.Key.ToString());

            var expGroups = typeGroup
                .GroupBy(d => d.ExposureTime)
                .OrderBy(g => g.Key);

            foreach (var expGroup in expGroups)
            {
                var expVm = new DarkGroupViewModel(expGroup.Key.ToString("F3", CultureInfo.InvariantCulture) + "s", typeVm);
                foreach (var tempCluster in BuildDarkTemperatureClusters(expGroup, DarkInventoryTemperatureHysteresisC))
                {
                    var tempVm = new DarkGroupViewModel(FormatDarkTemperatureClusterLabel(tempCluster), expVm);
                    foreach (var dark in tempCluster.OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
                        tempVm.Children.Add(new DarkFrameViewModel(dark, tempVm));
                    expVm.Children.Add(tempVm);
                }
                typeVm.Children.Add(expVm);
            }

            DarkInventory.Add(typeVm);
        }
        RegisterDarkSelectionTracking();

        Log($"Dark scan complete: {_darkCatalog.Count} dark frames cataloged");
        Log($"  Dark inventory temperature grouping uses +/-{DarkInventoryTemperatureHysteresisC:0.0} C hysteresis.");
        Log($"  Scanned {totalDirs} directories, found {totalFiles} image files (FITS={totalFits}, XISF={totalXisf}), {_darkCatalog.Count} darks suitable");

        if (_darkCatalog.Count == 0 && totalFiles > 0)
        {
            Log("  WARNING: Found files but no valid darks. Check file log at:");
            Log($"  {_sessionLogPath}");
            Log("  Common issues: wrong ImageType (Flat instead of Dark), missing exposure time");
        }

        UpdateScanSummaryStatusMessage();
        IsDarkScanActive = false;
        DarkScanProgressValue = 1;
        DarkScanProgressText = $"Dark scan: done ({totalDirs} directories, {totalFiles} files)";
    }

    private static bool AreRootsOnDifferentDrives(IEnumerable<string> flatRoots, IEnumerable<string> darkRoots)
    {
        static string? NormalizeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root)
                ? null
                : root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }

        var flatDriveRoots = flatRoots.Select(NormalizeRoot).Where(r => r != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var darkDriveRoots = darkRoots.Select(NormalizeRoot).Where(r => r != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (flatDriveRoots.Count == 0 || darkDriveRoots.Count == 0)
            return false;

        return !flatDriveRoots.Overlaps(darkDriveRoots);
    }

    private void UpdateScanSummaryStatusMessage()
    {
        var flatCount = _flatJobs.Sum(job => job.ExposureGroups.Sum(g => g.FilePaths.Count));
        var darkCount = _darkCatalog.Count;

        if (flatCount > 0 && darkCount > 0)
        {
            StatusMessage = $"{darkCount} darks found, {flatCount} flats found";
            return;
        }

        if (darkCount > 0)
        {
            StatusMessage = $"{darkCount} darks found";
            return;
        }

        if (flatCount > 0)
            StatusMessage = $"{flatCount} flats found";
    }

    private void InvalidateMatchingResultsAfterRescan()
    {
        if (MatchingDiagnostics.Count == 0 && MatchingGroups.Count == 0)
            return;

        _groupOverrideSelections.Clear();
        _groupIncludeSelections.Clear();
        MatchingDiagnostics = [];
        MatchingGroups = [];
        Log("[Match] Cleared previous matching results after re-scan. Run matching manually from the Matching Details tab.");
    }

    private static List<List<DarkFrame>> BuildDarkTemperatureClusters(IEnumerable<DarkFrame> darks, double hysteresisC)
    {
        var clusters = new List<List<DarkFrame>>();
        var sortedWithTemp = darks
            .Where(d => d.Temperature.HasValue)
            .OrderBy(d => d.Temperature!.Value)
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<DarkFrame>? current = null;
        double? lastTemp = null;
        foreach (var dark in sortedWithTemp)
        {
            var temp = dark.Temperature!.Value;
            if (current == null || !lastTemp.HasValue || Math.Abs(temp - lastTemp.Value) > hysteresisC + 1e-9)
            {
                current = [];
                clusters.Add(current);
            }

            current.Add(dark);
            lastTemp = temp;
        }

        var unknownTemp = darks
            .Where(d => !d.Temperature.HasValue)
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknownTemp.Count > 0)
            clusters.Add(unknownTemp);

        return clusters;
    }

    private static string FormatDarkTemperatureClusterLabel(IReadOnlyList<DarkFrame> cluster)
    {
        if (cluster.Count == 0)
            return "Temperature Unknown";

        var temps = cluster
            .Where(d => d.Temperature.HasValue)
            .Select(d => d.Temperature!.Value)
            .OrderBy(t => t)
            .ToList();

        if (temps.Count == 0)
            return $"Temperature Unknown ({cluster.Count})";

        var minTemp = temps.First();
        var maxTemp = temps.Last();
        if (Math.Abs(maxTemp - minTemp) < 0.05)
            return string.Format(CultureInfo.InvariantCulture, "Temperature {0:0.0} degrees C ({1})", minTemp, cluster.Count);

        return string.Format(
            CultureInfo.InvariantCulture,
            "Temperature {0:0.0} to {1:0.0} degrees C ({2})",
            minTemp,
            maxTemp,
            cluster.Count);
    }

    [RelayCommand]
    private async Task GenerateMatchingDiagnosticsAsync()
    {
        IsGeneratingMatching = true;
        MatchingProgressValue = 0;
        MatchingProgressText = "Matching 0/0 flats";
        try
        {
            if (_flatJobs.Count == 0 || _darkCatalog.Count == 0)
            {
                Log("No flats or darks to analyze for diagnostics. Scan both flat and dark trees first.");
                return;
            }

            SyncGroupOverrideSelectionsFromUi();
            var selectedDirectories = FlatDirectories
                .Where(vm => vm.IsSelected)
                .Select(vm => vm.DirectoryPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedJobsRaw = _flatJobs
                .Where(j => selectedDirectories.Contains(j.DirectoryPath))
                .ToList();
            // Keep all rows visible in Matching Details; "Include" is for processing selection.
            var selectedJobs = await BuildJobsWithGroupOverridesAsync(selectedJobsRaw, applyIncludeSelections: false);

            var selectedDarks = GetSelectedDarks();
            var darkMatchingOptions = _configuration.GetSection("DarkMatching").Get<DarkMatchingOptions>()
                ?? new DarkMatchingOptions();
            darkMatchingOptions = darkMatchingOptions with
            {
                MaxTempDeltaC = DarkTemperatureToleranceC,
                DarkOverBiasTempDeltaC = DarkOverBiasTempDeltaC,
                DarkOverBiasExposureDeltaSeconds = DarkOverBiasExposureDeltaSeconds
            };

            var matchingProgress = new Progress<MatchingProgress>(p =>
            {
                var total = Math.Max(0, p.TotalFlats);
                var processed = Math.Clamp(p.ProcessedFlats, 0, total);
                MatchingProgressValue = total > 0 ? (double)processed / total : 0;
                MatchingProgressText = $"Matching {processed}/{total} flats";
            });

            var diagnostics = await _darkMatcher.GenerateMatchingDiagnosticsAsync(
                selectedJobs, selectedDarks, darkMatchingOptions, matchingProgress);

            MatchingDiagnostics = new ObservableCollection<MatchingDiagnostic>(diagnostics);
            await BuildMatchingGroupsAsync(selectedJobs, selectedDarks, diagnostics);
            MatchingProgressValue = 1;
            MatchingProgressText = $"Matching complete ({diagnostics.Count} diagnostics)";
            Log($"Generated {diagnostics.Count} matching diagnostics");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating matching diagnostics");
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsGeneratingMatching = false;
        }
    }
}

