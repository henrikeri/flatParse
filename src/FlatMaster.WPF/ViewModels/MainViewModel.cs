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
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _requireDarks = true;

    // Deprecated: processing without flats controlled by matching priority; UI option removed
    private readonly IFileScannerService _fileScanner;
    private readonly IDarkMatchingService _darkMatcher;
    private readonly IPixInsightMasterDarkService _pixInsightMasterDarkService;
    private readonly IPixInsightService _pixInsight;
    private readonly IImageProcessingEngine _nativeEngine;
    private readonly IMetadataReaderService _metadataReader;
    private readonly IProcessingReportService _reportService;
    private readonly IOutputPathService _outputPathService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IConfiguration _configuration;

    [ObservableProperty]
    private string _pixInsightPath;

    [ObservableProperty]
    private bool _useNativeProcessing;

    [ObservableProperty]
    private bool _deleteCalibrated = true;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private bool _isCancellable;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private ObservableCollection<MatchingDiagnostic> _matchingDiagnostics = new();

    [ObservableProperty]
    private string _processingReportText = "";

    [ObservableProperty]
    private string _outputRootPath = "";

    [ObservableProperty]
    private bool _useReplicatedOutput = false;

    [ObservableProperty]
    private bool _copyOnlyProcessed = true;

    // â”€â”€ Batch progress tracking â”€â”€
    [ObservableProperty]
    private double _batchProgressValue; // 0.0 to 1.0

    [ObservableProperty]
    private string _batchProgressText = "";

    [ObservableProperty]
    private string _timeRemainingText = "";

    [ObservableProperty]
    private bool _showBatchProgress;

    private DateTime _processingStartTime;
    private int _completedBatches;
    private int _totalBatchCount;

    private static readonly Regex FolderHeaderRegex =
        new(@"Folders (\d+)-(\d+) of (\d+)\s+\((\d+)/(\d+) files\)", RegexOptions.Compiled);
    private static readonly Regex FolderDoneRegex =
        new(@"Folders \d+-\d+ (done|FAILED)\s+\((\d+)/(\d+) files\)", RegexOptions.Compiled);
    private static readonly Regex DarksExposureFolderRegex =
        new(@"^DARKS[\s_-]?\d+(?:\.\d+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ObservableCollection<string> FlatBaseRoots { get; } = new();
    public ObservableCollection<string> DarkLibraryRoots { get; } = new();
    public ObservableCollection<DirectoryJobViewModel> FlatDirectories { get; } = new();
    public ObservableCollection<DarkGroupViewModel> DarkInventory { get; } = new();

    private List<DirectoryJob> _flatJobs = new();
    private List<DarkFrame> _darkCatalog = new();

    public MainViewModel(
        IFileScannerService fileScanner,
        IDarkMatchingService darkMatcher,
        IPixInsightMasterDarkService pixInsightMasterDarkService,
        IPixInsightService pixInsight,
        IImageProcessingEngine nativeEngine,
        IMetadataReaderService metadataReader,
        IProcessingReportService reportService,
        IOutputPathService outputPathService,
        ILogger<MainViewModel> logger,
        IConfiguration configuration)
    {
        _fileScanner = fileScanner;
        _darkMatcher = darkMatcher;
        _pixInsightMasterDarkService = pixInsightMasterDarkService;
        _pixInsight = pixInsight;
        _nativeEngine = nativeEngine;
        _metadataReader = metadataReader;
        _reportService = reportService;
        _outputPathService = outputPathService;
        _logger = logger;
        _configuration = configuration;

        _pixInsightPath = _configuration["AppSettings:PixInsightExecutable"] ?? "";
        _outputRootPath = _configuration["OutputConfiguration:OutputRootPath"] ?? "D:\\fmOutput";
        if (bool.TryParse(_configuration["ProcessingDefaults:RequireDarks"], out var requireDarks))
            RequireDarks = requireDarks;
        
        var outputMode = _configuration["OutputConfiguration:Mode"] ?? "InlineInSource";
        _useReplicatedOutput = outputMode.Equals("ReplicatedSeparateTree", StringComparison.OrdinalIgnoreCase);
        
        Log($"FlatMaster v1.0.3 - Initialized at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Session log: {GetSessionLogPath()}");
    }

    [RelayCommand]
    private void AddFlatRoot()
    {
        var selectedPath = PickFolder("Select a base directory containing flat frames");
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        if (!FlatBaseRoots.Contains(selectedPath))
        {
            FlatBaseRoots.Add(selectedPath);
            Log($"Added flat root: {selectedPath}");
        }
    }

    [RelayCommand]
    private void RemoveFlatRoot(string? path)
    {
        if (path != null && FlatBaseRoots.Contains(path))
        {
            FlatBaseRoots.Remove(path);
            Log($"Removed flat root: {path}");
        }
    }

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        var selectedPath = PickFolder("Select output root directory");
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            OutputRootPath = selectedPath;
            Log($"Output directory set to: {selectedPath}");
        }
    }

    [RelayCommand]
    private void AddDarkRoot()
    {
        var selectedPath = PickFolder("Select a dark library directory");
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        if (!DarkLibraryRoots.Contains(selectedPath))
        {
            DarkLibraryRoots.Add(selectedPath);
            Log($"Added dark root: {selectedPath}");
        }
    }

    [RelayCommand]
    private void RemoveDarkRoot(string? path)
    {
        if (path != null && DarkLibraryRoots.Contains(path))
        {
            DarkLibraryRoots.Remove(path);
            Log($"Removed dark root: {path}");
        }
    }

    [RelayCommand]
    private void BrowsePixInsight()
    {
        var dialog = new WpfOpenFileDialog
        {
            Filter = "PixInsight Executable|PixInsight.exe",
            Title = "Select PixInsight.exe"
        };

        if (dialog.ShowDialog() == true)
        {
            PixInsightPath = dialog.FileName;
        }
    }

    private static string? PickFolder(string title)
    {
        var dialog = new WpfOpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return null;

        return string.IsNullOrWhiteSpace(dialog.FolderName) ? null : dialog.FolderName;
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _cancellationTokenSource?.Cancel();
        Log("Abort requested by user");
        StatusMessage = "Cancelling...";
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
        StatusMessage = "Scanning flat directories...";
        FlatDirectories.Clear();
        
        try
        {
            int totalDirs = 0;
            int totalFiles = 0;
            int totalFits = 0;
            int totalXisf = 0;
            var progress = new Progress<ScanProgress>(p =>
            {
                totalDirs = p.DirectoriesScanned;
                totalFiles = p.FilesFound;
                totalFits = p.FitsFound;
                totalXisf = p.XisfFound;
                StatusMessage = $"Scanning: {p.DirectoriesScanned} dirs, {p.FilesFound} files";
            });

            _flatJobs = await _fileScanner.ScanFlatDirectoriesAsync(FlatBaseRoots, progress, cancellationToken);

            /* Diagnostic-only logging and sample reads.
            Log($"DEBUG: Scanner returned {_flatJobs.Count} jobs");
            if (_flatJobs.Count > 0)
            {
                var firstJob = _flatJobs.First();
                Log($"DEBUG: First job - Dir: {firstJob.DirectoryPath}");
                Log($"DEBUG: First job - Groups: {firstJob.ExposureGroups.Count}");
                foreach (var grp in firstJob.ExposureGroups.Take(2))
                {
                    var typeLabel = grp.RepresentativeMetadata?.Type.ToString() ?? "Unknown";
                    Log($"DEBUG:   - {grp.ExposureTime}s: {grp.FilePaths.Count} files, Type={typeLabel}");
                }
            }

            try
            {
                var firstRoot = FlatBaseRoots.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstRoot) && Directory.Exists(firstRoot))
                {
                    var testDirs = new DirectoryInfo(firstRoot)
                        .EnumerateDirectories("*", SearchOption.AllDirectories)
                        .Take(3);

                    foreach (var dir in testDirs)
                    {
                        var testFiles = dir.GetFiles("*.fit*").Concat(dir.GetFiles("*.xisf")).Take(1).ToList();
                        if (testFiles.Any())
                        {
                            var testFile = testFiles[0];
                            var meta = await _metadataReader.ReadMetadataAsync(testFile.FullName, cancellationToken);
                            if (meta != null)
                            {
                                Log($"DEBUG SAMPLE: {testFile.Name} -> Type={meta.Type}, Exp={meta.ExposureTime}");
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception testEx)
            {
                Log($"DEBUG: Test read failed - {testEx.Message}");
            }
            */

            foreach (var job in _flatJobs)
            {
                FlatDirectories.Add(new DirectoryJobViewModel(job));
            }

            Log($"Flat scan complete: {_flatJobs.Count} directories with valid exposure groups");
            var totalFlats = _flatJobs.Sum(job => job.ExposureGroups.Sum(g => g.FilePaths.Count));
            Log($"  Scanned {totalDirs} directories, found {totalFiles} image files (FITS={totalFits}, XISF={totalXisf}), {totalFlats} flats suitable");
            
            if (_flatJobs.Count == 0 && totalFiles > 0)
            {
                Log($"  WARNING: Found files but no valid flat groups. Check file log at:");
                Log($"  {GetSessionLogPath()}");
                Log($"  Common issues: wrong ImageType (Dark/Bias instead of Flat), missing exposure, <3 files per group");
            }
            
            StatusMessage = $"Found {_flatJobs.Count} directories";
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
        StatusMessage = "Scanning dark library...";
        DarkInventory.Clear();
        
        try
        {
            int totalDirs = 0;
            int totalFiles = 0;
            int totalFits = 0;
            int totalXisf = 0;
            var progress = new Progress<ScanProgress>(p =>
            {
                totalDirs = p.DirectoriesScanned;
                totalFiles = p.FilesFound;
                totalFits = p.FitsFound;
                totalXisf = p.XisfFound;
                StatusMessage = $"Scanning darks: {p.DirectoriesScanned} dirs, {p.FilesFound} files";
            });

            _darkCatalog = await _fileScanner.ScanDarkLibraryAsync(DarkLibraryRoots, progress, cancellationToken);

            // Group by type, then exposure
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
                    foreach (var dark in expGroup.OrderBy(d => d.FilePath))
                    {
                        expVm.Children.Add(new DarkFrameViewModel(dark, expVm));
                    }
                    typeVm.Children.Add(expVm);
                }
                
                DarkInventory.Add(typeVm);
            }

            Log($"Dark scan complete: {_darkCatalog.Count} dark frames cataloged");
            Log($"  Scanned {totalDirs} directories, found {totalFiles} image files (FITS={totalFits}, XISF={totalXisf}), {_darkCatalog.Count} darks suitable");
            
            if (_darkCatalog.Count == 0 && totalFiles > 0)
            {
                Log($"  WARNING: Found files but no valid darks. Check file log at:");
                Log($"  {GetSessionLogPath()}");
                Log($"  Common issues: wrong ImageType (Flat instead of Dark), missing exposure time");
            }
            
            StatusMessage = $"Found {_darkCatalog.Count} dark frames";
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
        }
    }

    [RelayCommand]
    private void SelectAllFlats()
    {
        foreach (var dir in FlatDirectories)
        {
            dir.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAllFlats()
    {
        foreach (var dir in FlatDirectories)
        {
            dir.IsSelected = false;
        }
    }

    [RelayCommand]
    private void SelectAllDarks()
    {
        SetAllDarksSelection(true);
    }

    [RelayCommand]
    private void DeselectAllDarks()
    {
        SetAllDarksSelection(false);
    }

    private void SetAllDarksSelection(bool selected)
    {
        foreach (var typeGroup in DarkInventory)
        {
            typeGroup.IsSelected = selected;
            foreach (var expGroup in typeGroup.Children.OfType<DarkGroupViewModel>())
            {
                expGroup.IsSelected = selected;
                foreach (var dark in expGroup.Children.OfType<DarkFrameViewModel>())
                {
                    dark.IsSelected = selected;
                }
            }
        }
    }

    [RelayCommand]
    private async Task GenerateMatchingDiagnosticsAsync()
    {
        try
        {
            if (_flatJobs.Count == 0 || _darkCatalog.Count == 0)
            {
                Log("No flats or darks to analyze for diagnostics. Scan both flat and dark trees first.");
                return;
            }

            var selectedJobs = _flatJobs.Where(j =>
                FlatDirectories.Any(vm => vm.DirectoryPath == j.DirectoryPath && vm.IsSelected)).ToList();

            var selectedDarks = GetSelectedDarks();
            var darkMatchingOptions = _configuration.GetSection("DarkMatching").Get<DarkMatchingOptions>() 
                ?? new DarkMatchingOptions();

            var diagnostics = await _darkMatcher.GenerateMatchingDiagnosticsAsync(
                selectedJobs, selectedDarks, darkMatchingOptions);

            MatchingDiagnostics = new ObservableCollection<MatchingDiagnostic>(diagnostics);
            Log($"Generated {diagnostics.Count} matching diagnostics");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating matching diagnostics");
            Log($"ERROR: {ex.Message}");
        }
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

        var selectedJobs = _flatJobs.Where(j => 
            FlatDirectories.Any(vm => vm.DirectoryPath == j.DirectoryPath && vm.IsSelected)).ToList();

        if (selectedJobs.Count == 0)
        {
            WpfMessageBox.Show("Please select at least one flat directory.", "No Selection", 
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        var selectedDarks = GetSelectedDarks();
        if (selectedDarks.Count == 0 && RequireDarks)
        {
            WpfMessageBox.Show("No dark or bias frames are selected. Disable 'Require darks' to allow integration without calibration when no match is found.", "No Darks", 
                WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        if (selectedDarks.Count == 0)
        {
            Log("No darks selected; groups without a match will be integrated without dark subtraction.");
        }

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        IsProcessing = true;
        IsCancellable = true;
        StatusMessage = "Processing...";
        ShowBatchProgress = true;
        BatchProgressValue = 0;
        BatchProgressText = "Preparing...";
        TimeRemainingText = "";
        _completedBatches = 0;
        _totalBatchCount = 0;
        _processingStartTime = DateTime.Now;
        Log($"\n========== PROCESSING STARTED ==========");
        Log($"Jobs: {selectedJobs.Count}, Darks: {selectedDarks.Count}");

        try
        {
            var config = new ProcessingConfiguration
            {
                PixInsightExecutable = PixInsightPath,
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
                    MaxTempDeltaC = double.Parse(_configuration["DarkMatching:MaxTempDeltaC"] ?? "5.0", CultureInfo.InvariantCulture),
                    AllowNearestExposureWithOptimize = bool.Parse(_configuration["DarkMatching:AllowNearestExposureWithOptimize"] ?? "true")
                },
                RequireDarks = RequireDarks
            };

            var plan = new ProcessingPlan
            {
                Jobs = selectedJobs,
                DarkCatalog = selectedDarks,
                Configuration = config
            };

            // Safety policy: never write output artifacts into source input directories.
            // Always remap job outputs to explicit output root (fallback to temp if empty).
            var effectiveOutputRoot = OutputRootPath;
            if (string.IsNullOrWhiteSpace(effectiveOutputRoot))
            {
                effectiveOutputRoot = Path.Combine(Path.GetTempPath(), "FlatMasterOutput");
                OutputRootPath = effectiveOutputRoot;
                Log($"Output root was empty. Using fallback output root: {effectiveOutputRoot}");
            }

            if (!UseReplicatedOutput)
                Log("Inline source output is disabled. Writing all outputs to output root.");

            var remappedJobs = new List<DirectoryJob>();
            foreach (var job in selectedJobs)
            {
                remappedJobs.Add(new DirectoryJob
                {
                    DirectoryPath = job.DirectoryPath,
                    BaseRootPath = job.BaseRootPath,
                    OutputRootPath = effectiveOutputRoot,
                    RelativeDirectory = job.RelativeDirectory,
                    ExposureGroups = job.ExposureGroups,
                    IsSelected = job.IsSelected
                });
            }
            plan = new ProcessingPlan
            {
                Jobs = remappedJobs,
                DarkCatalog = selectedDarks,
                Configuration = config
            };
            Log($"Output mapped to: {effectiveOutputRoot}");

            var logProgress = new Progress<string>(msg => { Log(msg); ParseBatchProgress(msg); });

            if (selectedDarks.Count > 0)
            {
                var masterDarkOptions = _configuration.GetSection("PixInsightMasterDark").Get<PixInsightMasterDarkOptions>()
                    ?? _configuration.GetSection("NinaMasterDark").Get<PixInsightMasterDarkOptions>()
                    ?? new PixInsightMasterDarkOptions();

                var analysis = await _pixInsightMasterDarkService.PrepareMissingMasterDarksAsync(
                    plan.SelectedJobs,
                    selectedDarks,
                    config.DarkMatching,
                    effectiveOutputRoot,
                    masterDarkOptions,
                    executeBuilds: false,
                    logProgress,
                    cancellationToken);

                Log($"[MasterDark] Preflight analysis: missingGroups={analysis.MissingMasterDarkGroups}, buildable={analysis.BuildableMasterDarkCount}, issues={analysis.Issues.Count}");

                if (analysis.MissingMasterDarkGroups > 0)
                {
                    Log($"[MasterDark] Missing matching master darks for {analysis.MissingMasterDarkGroups} flat group(s).");
                }

                if (analysis.BuildableMasterDarkCount > 0)
                {
                    var previewText = BuildMasterDarkPlanPreview(analysis.BuildRequests, maxRequests: 8);
                    var totalRawFrames = analysis.BuildRequests.Sum(r => r.RawDarkPaths.Count);
                    var modeLabel = UseNativeProcessing
                        ? "Native"
                        : "PixInsight (same engine as flat processing)";

                    var confirmMessage =
                        "Missing matching master darks were detected.\n\n" +
                        $"FlatMaster will create {analysis.BuildableMasterDarkCount} master dark file(s) before flat processing.\n" +
                        $"Build mode: {modeLabel}\n" +
                        $"Total raw dark frames to combine: {totalRawFrames}\n" +
                        $"Output folder:\n{analysis.OutputDirectory}\n\n" +
                        "Planned builds:\n" + previewText + "\n\n" +
                        "Continue processing?";

                    var confirm = WpfMessageBox.Show(
                        confirmMessage,
                        "Master Dark Pre-Processing",
                        WpfMessageBoxButton.YesNo,
                        WpfMessageBoxImage.Information);

                    if (confirm != System.Windows.MessageBoxResult.Yes)
                    {
                        Log("Processing cancelled by user at master dark pre-processing prompt.");
                        StatusMessage = "Processing cancelled";
                        return;
                    }

                    MasterDarkPreparationResult prep;
                    if (UseNativeProcessing)
                    {
                        var nativeOnlyOptions = masterDarkOptions with
                        {
                            PreferPixInsightCli = false,
                            AllowInternalFallback = true
                        };

                        prep = await _pixInsightMasterDarkService.PrepareMissingMasterDarksAsync(
                            plan.SelectedJobs,
                            selectedDarks,
                            config.DarkMatching,
                            effectiveOutputRoot,
                            nativeOnlyOptions,
                            executeBuilds: true,
                            logProgress,
                            cancellationToken);

                        Log("[MasterDark] Native mode selected: generating missing masters with native integration.");
                    }
                    else
                    {
                        prep = await _pixInsight.BuildMasterDarksAsync(
                            analysis.BuildRequests,
                            analysis.OutputDirectory,
                            PixInsightPath,
                            config.XisfHintsMaster,
                            config.Rejection.LowSigma,
                            config.Rejection.HighSigma,
                            logProgress,
                            cancellationToken);

                        Log("[MasterDark] PixInsight mode selected: generating missing masters with PixInsight.");
                    }

                    foreach (var msg in prep.Messages)
                        Log(msg);

                    if (prep.RanPixInsightCli)
                        Log("[MasterDark] PixInsight CLI was invoked for master dark generation.");
                    if (prep.UsedInternalFallback)
                        Log("[MasterDark] Internal fallback was used for one or more master dark builds.");

                    foreach (var generated in prep.CreatedMasterDarks)
                    {
                        if (selectedDarks.Any(d => string.Equals(d.FilePath, generated.FilePath, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        selectedDarks.Add(generated);
                    }

                    if (prep.CreatedMasterDarkCount > 0)
                    {
                        Log($"[MasterDark] Added {prep.CreatedMasterDarkCount} generated/reused master dark(s) to catalog.");
                    }

                    if (prep.Issues.Count > 0)
                    {
                        foreach (var issue in prep.Issues)
                        {
                            Log($"[MasterDark] Unresolved {issue.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture)}s: {issue.Reason}");
                        }

                        if (RequireDarks)
                        {
                            var summary = string.Join(
                                Environment.NewLine,
                                prep.Issues.Take(12).Select(i =>
                                    $"- {i.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture)}s: {i.Reason}"));

                            throw new InvalidOperationException(
                                "Required dark calibration cannot proceed because one or more missing master darks " +
                                "could not be generated.\n" + summary);
                        }

                        Log("[MasterDark] Continuing in best-effort mode because 'Require darks' is disabled.");
                    }

                    plan = new ProcessingPlan
                    {
                        Jobs = remappedJobs,
                        DarkCatalog = selectedDarks,
                        Configuration = config
                    };
                }
                else if (analysis.Issues.Count > 0)
                {
                    foreach (var issue in analysis.Issues)
                    {
                        Log($"[MasterDark] Unresolved {issue.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture)}s: {issue.Reason}");
                    }

                    if (RequireDarks)
                    {
                        var summary = string.Join(
                            Environment.NewLine,
                            analysis.Issues.Take(12).Select(i =>
                                $"- {i.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture)}s: {i.Reason}"));

                        throw new InvalidOperationException(
                            "Required dark calibration cannot proceed because matching master darks are missing " +
                            "and no eligible raw dark groups were available to build them.\n" + summary);
                    }

                    Log("[MasterDark] No buildable master dark groups were found. Continuing in best-effort mode because 'Require darks' is disabled.");
                }
            }

            ProcessingResult result;

            if (UseNativeProcessing)
            {
                result = await _nativeEngine.ExecuteAsync(plan, logProgress, cancellationToken);
            }
            else
            {
                // Process folder-by-folder in batches (25 folders per PI launch)
                const int batchSize = 25;
                result = await _pixInsight.ProcessJobsInBatchesAsync(
                    plan, PixInsightPath, batchSize, logProgress, cancellationToken);
            }

            // Generate matching diagnostics
            Log("\nGenerating matching diagnostics...");
            var diagnostics = await _darkMatcher.GenerateMatchingDiagnosticsAsync(
                selectedJobs, selectedDarks, config.DarkMatching);
            MatchingDiagnostics = new ObservableCollection<MatchingDiagnostic>(diagnostics);
            Log($"Generated {diagnostics.Count} matching diagnostics");

            // Generate processing report
            var outputConfig = new OutputPathConfiguration
            {
                Mode = OutputMode.ReplicatedSeparateTree,
                OutputRootPath = effectiveOutputRoot,
                ReplicateDirectoryStructure = true,
                CopyOnlyProcessedFiles = CopyOnlyProcessed,
                DeleteCalibratedFlatsAfterMaster = DeleteCalibrated
            };

            var report = _reportService.GenerateReport(
                DateTime.UtcNow.AddSeconds(-1),
                diagnostics,
                selectedDarks,
                config,
                outputConfig);

            ProcessingReportText = _reportService.FormatReportAsText(report);
            Log("\n" + ProcessingReportText);

            if (result.Success)
            {
                Log($"\n========== PROCESSING COMPLETE ==========");
                if (result.TotalBatches > 0)
                    Log($"All {result.SucceededBatches}/{result.TotalBatches} batches succeeded.");
                else
                    Log("All master flats generated successfully!");
                WpfMessageBox.Show("Processing completed successfully!", "Success", 
                    WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            }
            else
            {
                Log($"\n========== PROCESSING FAILED ==========");
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
            Log($"\n========== PROCESSING CANCELLED ==========");
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
            IsProcessing = false;
            IsCancellable = false;
            ShowBatchProgress = false;
        }
    }

    private int _totalFolderCount;
    private int _totalFileCount;
    private int _lastFolderIdx;

    private void ParseBatchProgress(string msg)
    {
        var headerMatch = FolderHeaderRegex.Match(msg);
        if (headerMatch.Success)
        {
            _lastFolderIdx = int.Parse(headerMatch.Groups[2].Value);
            _totalFolderCount = int.Parse(headerMatch.Groups[3].Value);
            int filesUpcoming = int.Parse(headerMatch.Groups[4].Value);
            _totalFileCount = int.Parse(headerMatch.Groups[5].Value);
            BatchProgressText = $"Folder {_lastFolderIdx}/{_totalFolderCount}  ({filesUpcoming}/{_totalFileCount} files)";
            StatusMessage = $"Processing folders {headerMatch.Groups[1].Value}-{_lastFolderIdx} of {_totalFolderCount}...";
        }

        var doneMatch = FolderDoneRegex.Match(msg);
        if (doneMatch.Success)
        {
            _completedBatches++;
            int filesDone = int.Parse(doneMatch.Groups[2].Value);
            int filesTotal = int.Parse(doneMatch.Groups[3].Value);
            _totalFileCount = filesTotal;

            if (filesTotal > 0)
            {
                BatchProgressValue = (double)filesDone / filesTotal;
                var elapsed = DateTime.Now - _processingStartTime;
                var avgPerFile = elapsed.TotalSeconds / Math.Max(1, filesDone);
                var remaining = avgPerFile * (filesTotal - filesDone);
                TimeRemainingText = FormatTimeRemaining(TimeSpan.FromSeconds(remaining));
                BatchProgressText = $"Folder {_lastFolderIdx}/{_totalFolderCount}  ({filesDone}/{filesTotal} files, {(int)(BatchProgressValue * 100)}%)";
            }
        }
    }

    private static string FormatTimeRemaining(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return "Almost done";
        if (ts.TotalHours >= 1)
            return $"~{(int)ts.TotalHours}h {ts.Minutes}m remaining";
        if (ts.TotalMinutes >= 1)
            return $"~{(int)ts.TotalMinutes}m remaining";
        return "< 1 min remaining";
    }

    private static string BuildMasterDarkPlanPreview(IReadOnlyList<MasterDarkBuildRequest> requests, int maxRequests)
    {
        if (requests.Count == 0)
            return "(none)";

        var sb = new StringBuilder();
        var limit = Math.Max(1, maxRequests);
        var shown = Math.Min(limit, requests.Count);

        for (int i = 0; i < shown; i++)
        {
            var request = requests[i];
            var binning = string.IsNullOrWhiteSpace(request.Binning) ? "n/a" : request.Binning;
            var treeRoot = GetMasterDarkSourceTreeRoot(request.RawDarkPaths);
            sb.AppendLine($"{i + 1}. {request.TargetMasterType} {request.ExposureTime.ToString("0.###", CultureInfo.InvariantCulture)}s, bin {binning}");
            sb.AppendLine($"   Combine: {request.RawDarkPaths.Count} raw dark file(s)");
            sb.AppendLine($"   Source tree: {treeRoot}");
        }

        if (requests.Count > shown)
            sb.Append($"... plus {requests.Count - shown} more build(s).");

        return sb.ToString().TrimEnd();
    }

    private static string GetMasterDarkSourceTreeRoot(IReadOnlyList<string> rawDarkPaths)
    {
        if (rawDarkPaths.Count == 0)
            return "(unknown)";

        var exposureRoots = rawDarkPaths
            .Select(TryGetExposureTreeRoot)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (exposureRoots.Count == 1)
            return exposureRoots[0];

        if (exposureRoots.Count > 1)
        {
            var commonExposureRoot = GetCommonPathPrefix(exposureRoots);
            if (!string.IsNullOrWhiteSpace(commonExposureRoot))
                return commonExposureRoot;

            var sample = string.Join(" | ", exposureRoots.Take(3));
            if (exposureRoots.Count > 3)
                sample += $" | ... ({exposureRoots.Count} trees)";
            return sample;
        }

        var directories = rawDarkPaths
            .Select(Path.GetDirectoryName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
            return "(unknown)";

        var commonDirectory = GetCommonPathPrefix(directories);
        return string.IsNullOrWhiteSpace(commonDirectory) ? directories[0] : commonDirectory;
    }

    private static string? TryGetExposureTreeRoot(string rawDarkPath)
    {
        var directory = Path.GetDirectoryName(rawDarkPath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var normalized = directory.Replace('/', '\\').TrimEnd('\\');
        var isUnc = normalized.StartsWith("\\\\", StringComparison.Ordinal);
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        for (int i = 0; i < parts.Length; i++)
        {
            if (!DarksExposureFolderRegex.IsMatch(parts[i]))
                continue;

            var prefix = string.Join('\\', parts.Take(i + 1));
            if (isUnc)
                return "\\\\" + prefix;
            if (parts[0].EndsWith(":", StringComparison.Ordinal) && i == 0)
                return parts[0] + "\\";
            return prefix;
        }

        return null;
    }

    private static string GetCommonPathPrefix(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return string.Empty;

        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('/', '\\').TrimEnd('\\'))
            .ToList();
        if (normalized.Count == 0)
            return string.Empty;

        var isUnc = normalized.All(p => p.StartsWith("\\\\", StringComparison.Ordinal));
        var splitPaths = normalized
            .Select(p => p.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .ToList();

        if (splitPaths.Count == 0)
            return string.Empty;

        var minLength = splitPaths.Min(parts => parts.Length);
        int commonLength = 0;

        for (int i = 0; i < minLength; i++)
        {
            var token = splitPaths[0][i];
            if (!splitPaths.All(parts => string.Equals(parts[i], token, StringComparison.OrdinalIgnoreCase)))
                break;
            commonLength++;
        }

        if (commonLength == 0)
            return string.Empty;

        var prefix = string.Join('\\', splitPaths[0].Take(commonLength));

        if (isUnc)
            return "\\\\" + prefix;
        if (commonLength == 1 && splitPaths[0][0].EndsWith(":", StringComparison.Ordinal))
            return prefix + "\\";

        return prefix;
    }

    private List<DarkFrame> GetSelectedDarks()
    {
        var selected = new List<DarkFrame>();
        
        foreach (var typeGroup in DarkInventory)
        {
            foreach (var expGroup in typeGroup.Children.OfType<DarkGroupViewModel>())
            {
                foreach (var darkVm in expGroup.Children.OfType<DarkFrameViewModel>())
                {
                    if (darkVm.IsSelected)
                    {
                        selected.Add(darkVm.DarkFrame);
                    }
                }
            }
        }
        
        return selected;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{timestamp}] {message}";
        LogText += line + Environment.NewLine;
        
        // Also write to file
        try
        {
            File.AppendAllText(GetSessionLogPath(), line + Environment.NewLine);
        }
        catch { }
    }

    private static string GetSessionLogPath()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(Path.GetTempPath(), $"FlatMaster_{timestamp}.log");
    }
}

// Supporting ViewModels
public partial class DirectoryJobViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public string DirectoryPath { get; }
    public string DisplayPath { get; }
    public int GroupCount { get; }
    public int FileCount { get; }

    public DirectoryJobViewModel(DirectoryJob job)
    {
        DirectoryPath = job.DirectoryPath;
        DisplayPath = job.RelativeDirectory ?? Path.GetFileName(job.DirectoryPath);
        GroupCount = job.ValidGroupCount;
        FileCount = job.TotalFileCount;
    }
}

public partial class DarkGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public string Name { get; }
    public ObservableCollection<object> Children { get; } = new();
    public DarkGroupViewModel? Parent { get; }
    private bool _isSyncingSelection;

    public DarkGroupViewModel(string name, DarkGroupViewModel? parent = null)
    {
        Name = name;
        Parent = parent;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_isSyncingSelection)
            return;

        _isSyncingSelection = true;
        try
        {
            foreach (var child in Children)
            {
                if (child is DarkGroupViewModel group)
                    group.SetSelectedRecursive(value);
                else if (child is DarkFrameViewModel frame)
                    frame.SetSelectedFromParent(value);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }

        Parent?.SyncFromChildren();
    }

    internal void SetSelectedRecursive(bool value)
    {
        _isSyncingSelection = true;
        try
        {
            if (IsSelected != value)
                IsSelected = value;

            foreach (var child in Children)
            {
                if (child is DarkGroupViewModel group)
                    group.SetSelectedRecursive(value);
                else if (child is DarkFrameViewModel frame)
                    frame.SetSelectedFromParent(value);
            }
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    internal void SyncFromChildren()
    {
        if (_isSyncingSelection || Children.Count == 0)
            return;

        var allSelected = true;
        foreach (var child in Children)
        {
            var isChildSelected = child switch
            {
                DarkGroupViewModel group => group.IsSelected,
                DarkFrameViewModel frame => frame.IsSelected,
                _ => false
            };

            if (!isChildSelected)
            {
                allSelected = false;
                break;
            }
        }

        _isSyncingSelection = true;
        try
        {
            if (IsSelected != allSelected)
                IsSelected = allSelected;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        Parent?.SyncFromChildren();
    }
}

public partial class DarkFrameViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public DarkFrame DarkFrame { get; }
    public DarkGroupViewModel Parent { get; }
    public string FileName => Path.GetFileName(DarkFrame.FilePath);
    private bool _isSyncingSelection;

    public DarkFrameViewModel(DarkFrame darkFrame, DarkGroupViewModel parent)
    {
        DarkFrame = darkFrame;
        Parent = parent;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_isSyncingSelection)
            return;

        Parent.SyncFromChildren();
    }

    internal void SetSelectedFromParent(bool value)
    {
        _isSyncingSelection = true;
        try
        {
            if (IsSelected != value)
                IsSelected = value;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }
}

