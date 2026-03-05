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
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    private sealed class DelegatingProgress<T>(Action<T> reportAction) : IProgress<T>
    {
        private readonly Action<T> _reportAction = reportAction;
        public void Report(T value) => _reportAction(value);
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _cancellationTokenSource?.Cancel();
        Log("Abort requested by user");
        StatusMessage = "Cancelling...";
    }

    [RelayCommand]
    private async Task ProcessSelectedAsync()
    {
        if (!UseNativeProcessing && !File.Exists(PixInsightPath))
        {
            WpfMessageBox.Show("Please specify a valid PixInsight executable.", "Invalid Path",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Warning);
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

        if (!DarksOnlyProcessMode && selectedJobsRaw.Count == 0)
        {
            WpfMessageBox.Show("Please select at least one flat directory.", "No Selection",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        var selectedDarks = GetSelectedDarks();
        if (selectedDarks.Count == 0 && (DarksOnlyProcessMode || RequireDarks))
        {
            var message = DarksOnlyProcessMode
                ? "No dark frames are selected. Select dark frames from Scan & Match before starting darks-only processing."
                : "No dark or bias frames are selected. Disable 'Require darks' to allow integration without calibration when no match is found.";
            WpfMessageBox.Show(message, "No Darks",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        if (!DarksOnlyProcessMode && selectedDarks.Count == 0)
            Log("No darks selected; groups without a match will be integrated without dark subtraction.");

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        var selectedJobs = await BuildJobsWithGroupOverridesAsync(selectedJobsRaw, cancellationToken);

        IsProcessing = true;
        IsCancellable = true;
        StatusMessage = "Processing...";
        ShowBatchProgress = true;
        BatchProgressValue = 0;
        BatchProgressText = "Preparing...";
        TimeRemainingText = "";
        _completedBatches = 0;
        _processingStartTime = DateTime.Now;
        StartProcessingLogDrain();
        if (UseNativeProcessing)
            InitializeNativeProgressTracking(selectedJobs);
        else
            ResetNativeProgressTracking();
        var reportStartUtc = DateTime.UtcNow;
        Log("\n========== PROCESSING STARTED ==========");
        Log($"Jobs: {selectedJobs.Count}, Darks: {selectedDarks.Count}");
        if (DarksOnlyProcessMode)
            Log("Mode: Darks-only (master dark generation only)");

        try
        {
            var config = BuildProcessingConfiguration();
            var effectiveOutputRoot = ResolveEffectiveOutputRoot();
            var plan = BuildProcessingPlan(selectedJobs, selectedDarks, config, effectiveOutputRoot);
            Log($"Output mapped to: {effectiveOutputRoot}");

            var logProgress = new DelegatingProgress<string>(EnqueueProcessingLogMessage);

            if (DarksOnlyProcessMode)
            {
                await RunDarksOnlyProcessingAsync(plan, effectiveOutputRoot, cancellationToken, logProgress);
                return;
            }

            await MaterializeRequiredMastersAsync(plan, effectiveOutputRoot, cancellationToken, logProgress);

            ProcessingResult result;
            if (UseNativeProcessing)
            {
                result = await _nativeEngine.ExecuteAsync(plan, logProgress, cancellationToken);
            }
            else
            {
                const int batchSize = 25;
                result = await _pixInsight.ProcessJobsInBatchesAsync(
                    plan, PixInsightPath, batchSize, logProgress, cancellationToken);
            }

            Log("\nGenerating matching diagnostics...");
            var diagnostics = await _darkMatcher.GenerateMatchingDiagnosticsAsync(
                selectedJobs, selectedDarks, config.DarkMatching);
            MatchingDiagnostics = new ObservableCollection<MatchingDiagnostic>(diagnostics);
            await BuildMatchingGroupsAsync(selectedJobs, selectedDarks, diagnostics, cancellationToken);
            Log($"Generated {diagnostics.Count} matching diagnostics");

            var outputConfig = new OutputPathConfiguration
            {
                Mode = OutputMode.ReplicatedSeparateTree,
                OutputRootPath = effectiveOutputRoot,
                ReplicateDirectoryStructure = true,
                CopyOnlyProcessedFiles = CopyOnlyProcessed,
                DeleteCalibratedFlatsAfterMaster = DeleteCalibrated
            };

            var reportText = await Task.Run(() =>
            {
                var report = _reportService.GenerateReport(
                    reportStartUtc,
                    diagnostics,
                    selectedDarks,
                    config,
                    outputConfig);
                return _reportService.FormatReportAsText(report);
            }, cancellationToken);

            ProcessingReportText = reportText;
            Log("\n" + ProcessingReportText);

            if (result.Success)
            {
                Log("\n========== PROCESSING COMPLETE ==========");
                if (result.TotalBatches > 0)
                    Log($"All {result.SucceededBatches}/{result.TotalBatches} batches succeeded.");
                else
                    Log("All master flats generated successfully!");
                WpfMessageBox.Show("Processing completed successfully!", "Success",
                    WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            }
            else
            {
                Log("\n========== PROCESSING FAILED ==========");
                if (result.TotalBatches > 0)
                    Log($"Batches: {result.SucceededBatches} succeeded, {result.FailedBatches} failed of {result.TotalBatches} total.");
                Log($"Exit code: {result.ExitCode}");
                Log($"Error: {result.ErrorMessage}");
                WpfMessageBox.Show($"Processing failed:\n{result.ErrorMessage}", "Error",
                    WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            }

            StatusMessage = result.Success ? "Processing complete" : "Processing failed";
        }
        catch (OperationCanceledException)
        {
            Log("\n========== PROCESSING CANCELLED ==========");
            Log("Processing cancelled by user");
            StatusMessage = "Processing cancelled";
            WpfMessageBox.Show("Processing was cancelled.", "Cancelled",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing error");
            Log($"EXCEPTION: {ex.Message}");
            WpfMessageBox.Show($"Processing error: {ex.Message}", "Error",
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            StatusMessage = "Error";
        }
        finally
        {
            FlushAllQueuedProcessingLogMessages();
            StopProcessingLogDrain();
            ResetNativeProgressTracking();
            IsProcessing = false;
            IsCancellable = false;
            ShowBatchProgress = false;
        }
    }

    private ProcessingConfiguration BuildProcessingConfiguration()
    {
        return new ProcessingConfiguration
        {
            PixInsightExecutable = PixInsightPath,
            OutputFileExtension = string.Equals(OutputFormat, "FITS", StringComparison.OrdinalIgnoreCase) ? "fits" : "xisf",
            DeleteCalibratedFlats = DeleteCalibrated,
            CacheDirName = _configuration["ProcessingDefaults:CacheDirName"] ?? "_DarkMasters",
            CalibratedSubdirBase = _configuration["ProcessingDefaults:CalibratedSubdirBase"] ?? "_CalibratedFlats",
            XisfHintsMaster = _configuration["ProcessingDefaults:XisfHintsMaster"] ?? "",
            Rejection = new RejectionSettings
            {
                LowSigma = double.Parse(_configuration["ProcessingDefaults:RejectionLowSigma"] ?? "5.0", CultureInfo.InvariantCulture),
                HighSigma = double.Parse(_configuration["ProcessingDefaults:RejectionHighSigma"] ?? "5.0", CultureInfo.InvariantCulture)
            },
            DarkMatching = new DarkMatchingOptions
            {
                EnforceBinning = bool.Parse(_configuration["DarkMatching:EnforceBinning"] ?? "true"),
                PreferSameGainOffset = bool.Parse(_configuration["DarkMatching:PreferSameGainOffset"] ?? "true"),
                PreferClosestTemp = bool.Parse(_configuration["DarkMatching:PreferClosestTemp"] ?? "true"),
                MaxTempDeltaC = DarkTemperatureToleranceC,
                DarkOverBiasTempDeltaC = DarkOverBiasTempDeltaC,
                DarkOverBiasExposureDeltaSeconds = DarkOverBiasExposureDeltaSeconds,
                AllowNearestExposureWithOptimize = bool.Parse(_configuration["DarkMatching:AllowNearestExposureWithOptimize"] ?? "true")
            },
            RequireDarks = RequireDarks
        };
    }

    private string ResolveEffectiveOutputRoot()
    {
        var effectiveOutputRoot = OutputRootPath;
        if (string.IsNullOrWhiteSpace(effectiveOutputRoot))
        {
            effectiveOutputRoot = Path.Combine(Path.GetTempPath(), "FlatMasterOutput");
            OutputRootPath = effectiveOutputRoot;
            Log($"Output root was empty. Using fallback output root: {effectiveOutputRoot}");
        }

        if (!UseReplicatedOutput)
            Log("Inline source output is disabled. Writing all outputs to output root.");

        return effectiveOutputRoot;
    }

    private ProcessingPlan BuildProcessingPlan(
        List<DirectoryJob> selectedJobs,
        List<DarkFrame> selectedDarks,
        ProcessingConfiguration config,
        string outputRoot)
    {
        var remappedJobs = new List<DirectoryJob>();

        if (DarksOnlyProcessMode)
        {
            remappedJobs.Add(new DirectoryJob
            {
                DirectoryPath = outputRoot,
                BaseRootPath = outputRoot,
                OutputRootPath = outputRoot,
                RelativeDirectory = "__DARKS_ONLY__",
                ExposureGroups = [],
                IsSelected = true
            });
        }
        else
        {
            foreach (var job in selectedJobs)
            {
                remappedJobs.Add(new DirectoryJob
                {
                    DirectoryPath = job.DirectoryPath,
                    BaseRootPath = job.BaseRootPath,
                    OutputRootPath = outputRoot,
                    RelativeDirectory = job.RelativeDirectory,
                    ExposureGroups = job.ExposureGroups,
                    IsSelected = job.IsSelected
                });
            }
        }

        return new ProcessingPlan
        {
            Jobs = remappedJobs,
            DarkCatalog = selectedDarks,
            Configuration = config
        };
    }

    private async Task RunDarksOnlyProcessingAsync(
        ProcessingPlan plan,
        string effectiveOutputRoot,
        CancellationToken cancellationToken,
        IProgress<string> logProgress)
    {
        try
        {
            StatusMessage = "Preparing darks-only materialization...";
            Log("[DarksOnly] Previewing dark groups by exposure and temperature (+/-1.0 C).");
            var preview = await Task.Run(
                () => _masterMaterializer.PreviewDarksOnlyMaterializationAsync(plan, DarkTemperatureToleranceC, cancellationToken),
                cancellationToken);
            if (preview.Count == 0)
            {
                Log("[DarksOnly] No eligible raw dark groups found.");
                WpfMessageBox.Show("No eligible raw dark groups found.", "Darks-only Mode",
                    WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
                StatusMessage = "No dark groups found";
                return;
            }

            var grouped = preview
                .GroupBy(c => new { c.ExposureSeconds, c.TemperatureC })
                .OrderBy(g => g.Key.ExposureSeconds ?? double.NaN)
                .ThenBy(g => g.Key.TemperatureC ?? double.PositiveInfinity)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Darks-only mode will build up to {grouped.Count} master dark(s):");
            sb.AppendLine($"  Output root: {effectiveOutputRoot}");
            sb.AppendLine();
            foreach (var g in grouped.Take(60))
            {
                var exp = g.Key.ExposureSeconds.HasValue
                    ? g.Key.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s"
                    : "unknown";
                var temp = g.Key.TemperatureC.HasValue
                    ? g.Key.TemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture) + " C"
                    : "unknown";
                var frames = g.Sum(x => x.FrameCount);
                sb.AppendLine($"  - Exp {exp}, Temp {temp}: {frames} frames");
            }

            sb.AppendLine();
            sb.AppendLine("Proceed to create these master darks now? (Yes = create, No = cancel)");

            var userChoice = WpfMessageBox.Show(sb.ToString(), "Build Master Darks?", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question);
            if (userChoice != System.Windows.MessageBoxResult.Yes)
            {
                Log("[DarksOnly] Master dark creation cancelled by user.");
                StatusMessage = "Processing cancelled";
                throw new OperationCanceledException("User cancelled darks-only creation");
            }

            Log("[DarksOnly] Materialization stage: building master darks now.");
            var masters = await Task.Run(
                () => _masterMaterializer.MaterializeDarksOnlyAsync(plan, UseNativeProcessing, PixInsightPath, DarkTemperatureToleranceC, cancellationToken, logProgress),
                cancellationToken);
            Log($"[DarksOnly] Materialized {masters.Count} master dark(s).");
            StatusMessage = "Processing complete";
            WpfMessageBox.Show($"Darks-only processing completed. Materialized {masters.Count} master dark(s).",
                "Success", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing cancelled";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Darks-only materialization failed");
            WpfMessageBox.Show($"Darks-only materialization failed: {ex.Message}", "Darks-only Error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            Log($"ERROR: Darks-only materialization failed - {ex.Message}");
            throw;
        }
    }

    private async Task MaterializeRequiredMastersAsync(
        ProcessingPlan plan,
        string effectiveOutputRoot,
        CancellationToken cancellationToken,
        IProgress<string> logProgress)
    {
        try
        {
            StatusMessage = "Preparing master darks...";
            Log("[MasterDark] Previewing required master darks from DARKS/<folder>/ as first step.");

            var candidates = await Task.Run(
                () => _masterMaterializer.PreviewMaterializationAsync(plan, [.. DarkLibraryRoots], cancellationToken),
                cancellationToken);
            if (candidates.Count > 0)
            {
                var groups = candidates
                    .GroupBy(c => c.ExposureSeconds ?? double.NaN)
                    .OrderBy(g => g.Key);

                var sb = new StringBuilder();
                sb.AppendLine($"The application will build {groups.Count()} master dark(s) and store them under the output root:");
                sb.AppendLine($"  Output root: {effectiveOutputRoot}");
                sb.AppendLine();

                foreach (var g in groups.Take(50))
                {
                    var key = double.IsNaN(g.Key)
                        ? "unknown"
                        : Math.Abs(g.Key - Math.Round(g.Key)) < 0.001
                            ? ((int)Math.Round(g.Key)).ToString(CultureInfo.InvariantCulture) + "s"
                            : g.Key.ToString("0.000", CultureInfo.InvariantCulture) + "s";

                    var totalFrames = g.Sum(x => x.FrameCount);
                    var folders = g.Count();
                    sb.AppendLine($"  - {key}: {totalFrames} frames (from {folders} folder{(folders == 1 ? "" : "s")})");
                }

                sb.AppendLine();
                sb.AppendLine("Proceed to create these master darks now? (Yes = create and continue, No = cancel processing)");

                var userChoice = WpfMessageBox.Show(sb.ToString(), "Build Master Darks?", WpfMessageBoxButton.YesNo, WpfMessageBoxImage.Question);
                if (userChoice != System.Windows.MessageBoxResult.Yes)
                {
                    Log("Master dark materialization cancelled by user");
                    StatusMessage = "Processing cancelled";
                    throw new OperationCanceledException("User cancelled master dark creation");
                }
            }
            else
            {
                Log("[MasterDark] No master darks needed (existing masters or no raw-dark folders matched).");
            }

            Log("[MasterDark] Materialization stage: building required master darks now.");
            var masters = await Task.Run(
                () => _masterMaterializer.MaterializeMastersAsync(plan, [.. DarkLibraryRoots], UseNativeProcessing, PixInsightPath, cancellationToken, logProgress),
                cancellationToken);
            if (masters.Count > 0)
                Log($"[MasterDark] Materialized {masters.Count} master dark(s) and stored under output root.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Processing cancelled";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Master dark materialization failed");
            WpfMessageBox.Show($"Master dark creation failed: {ex.Message}", "Master Dark Error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
            Log($"ERROR: Master dark materialization failed - {ex.Message}");
            throw;
        }
    }
}

