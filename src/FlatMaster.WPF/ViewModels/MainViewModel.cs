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
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const double DarkTemperatureToleranceC = 1.0;
    private const double DarkInventoryTemperatureHysteresisC = 2.0;
    private static readonly Brush TabStepIdleBrush = CreateFrozenBrush("#607D8B");
    private static readonly Brush TabStepActiveBrush = CreateFrozenBrush("#1976D2");
    private static readonly Brush TabStepDoneBrush = CreateFrozenBrush("#2E7D32");
    private static readonly Brush TabStepDisabledBrush = CreateFrozenBrush("#9E9E9E");

    [ObservableProperty]
    private bool _requireDarks = true;

    [ObservableProperty]
    private bool _darksOnlyProcessMode;

    private readonly IFileScannerService _fileScanner;
    private readonly IDarkMatchingService _darkMatcher;
    private readonly IPixInsightService _pixInsight;
    private readonly IImageProcessingEngine _nativeEngine;
    private readonly IProcessingReportService _reportService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IMasterDarkMaterializer _masterMaterializer;
    private readonly IConfiguration _configuration;
    private readonly string _sessionLogPath;
    private readonly string _userSettingsPath;

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
    private ObservableCollection<MatchingDiagnostic> _matchingDiagnostics = [];

    [ObservableProperty]
    private ObservableCollection<MatchingGroupViewModel> _matchingGroups = [];

    [ObservableProperty]
    private string _processingReportText = "";

    [ObservableProperty]
    private string _outputRootPath = "";

    [ObservableProperty]
    private bool _useReplicatedOutput;

    [ObservableProperty]
    private bool _copyOnlyProcessed = true;

    [ObservableProperty]
    private string _outputFormat = "XISF";

    [ObservableProperty]
    private double _batchProgressValue;

    [ObservableProperty]
    private string _batchProgressText = "";

    [ObservableProperty]
    private string _timeRemainingText = "";

    [ObservableProperty]
    private bool _isFlatScanActive;

    [ObservableProperty]
    private bool _isDarkScanActive;

    [ObservableProperty]
    private string _flatScanProgressText = "Flat scan: idle";

    [ObservableProperty]
    private string _darkScanProgressText = "Dark scan: idle";

    [ObservableProperty]
    private double _flatScanProgressValue;

    [ObservableProperty]
    private double _darkScanProgressValue;

    [ObservableProperty]
    private double _darkOverBiasTempDeltaC = 5.0;

    [ObservableProperty]
    private double _darkOverBiasExposureDeltaSeconds = 5.0;

    [ObservableProperty]
    private bool _showBatchProgress;

    [ObservableProperty]
    private bool _isGeneratingMatching;

    [ObservableProperty]
    private double _matchingProgressValue;

    [ObservableProperty]
    private string _matchingProgressText = "";

    private DateTime _processingStartTime;
    private int _completedBatches;
    private readonly Dictionary<string, string?> _groupOverrideSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupIncludeSelections = new(StringComparer.OrdinalIgnoreCase);

    private int _totalFolderCount;
    private int _totalFileCount;
    private int _lastFolderIdx;
    private bool _nativeProgressActive;
    private int _nativeTotalGroups;
    private int _nativeCompletedGroups;
    private int _nativeCurrentGroupFrames;
    private int _nativeCurrentGroupCalibrated;
    private bool _nativeGroupInFlight;

    private static readonly Regex FolderHeaderRegex =
        MyRegex();
    private static readonly Regex FolderDoneRegex =
        MyRegex1();
    private static readonly Regex NativeLoadingRegex =
        MyRegex2();
    private static readonly Regex NativeCalibratedRegex =
        MyRegex3();
    private static readonly Regex NativeMasterWrittenRegex =
        MyRegex4();
    private static readonly Regex NativeSkippedRegex =
        MyRegex5();
    private static readonly Regex NativeErrorRegex =
        MyRegex6();

    public ObservableCollection<string> FlatBaseRoots { get; } = [];
    public ObservableCollection<string> DarkLibraryRoots { get; } = [];
    public ObservableCollection<DirectoryJobViewModel> FlatDirectories { get; } = [];
    public ObservableCollection<DarkGroupViewModel> DarkInventory { get; } = [];
    public ObservableCollection<string> OutputFormatOptions { get; } = ["XISF", "FITS"];

    private List<DirectoryJob> _flatJobs = [];
    private List<DarkFrame> _darkCatalog = [];

    public bool IsNotDarksOnlyMode => !DarksOnlyProcessMode;
    public string ScanButtonText => DarksOnlyProcessMode ? "Scan Dark Library" : "Scan Flats + Darks";
    public bool CanScanAll => !IsScanning;
    public bool CanScanFlats => !IsScanning && !DarksOnlyProcessMode;
    public bool CanScanDarks => !IsScanning;
    public Brush ConfigurationTabStepBrush => IsConfigurationComplete ? TabStepDoneBrush : TabStepIdleBrush;
    public Brush ScanTabStepBrush => IsScanning ? TabStepActiveBrush : HasScanResults ? TabStepDoneBrush : TabStepIdleBrush;
    public Brush MatchingTabStepBrush
    {
        get
        {
            if (DarksOnlyProcessMode)
                return TabStepDisabledBrush;
            if (IsGeneratingMatching)
                return TabStepActiveBrush;
            return HasMatchingResults ? TabStepDoneBrush : TabStepIdleBrush;
        }
    }

    public Brush ProcessingTabStepBrush => IsProcessing ? TabStepActiveBrush : HasProcessResults ? TabStepDoneBrush : TabStepIdleBrush;

    private bool IsConfigurationComplete
    {
        get
        {
            var hasDarkRoot = DarkLibraryRoots.Count > 0;
            if (DarksOnlyProcessMode)
                return hasDarkRoot;

            var hasFlatRoot = FlatBaseRoots.Count > 0;
            var hasEngine = UseNativeProcessing || (!string.IsNullOrWhiteSpace(PixInsightPath) && File.Exists(PixInsightPath));
            var hasOutput = !string.IsNullOrWhiteSpace(OutputRootPath);
            return hasFlatRoot && hasDarkRoot && hasEngine && hasOutput;
        }
    }

    private bool HasScanResults => DarksOnlyProcessMode ? _darkCatalog.Count > 0 : _flatJobs.Count > 0 && _darkCatalog.Count > 0;
    private bool HasMatchingResults => MatchingGroups.Count > 0 || MatchingDiagnostics.Count > 0;
    private bool HasProcessResults => !string.IsNullOrWhiteSpace(ProcessingReportText);

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScanAll));
        OnPropertyChanged(nameof(CanScanFlats));
        OnPropertyChanged(nameof(CanScanDarks));
        RaiseTabWorkflowStateChanged();
        if (!value)
        {
            if (!IsFlatScanActive && FlatScanProgressValue <= 0)
                FlatScanProgressText = "Flat scan: idle";
            if (!IsDarkScanActive && DarkScanProgressValue <= 0)
                DarkScanProgressText = "Dark scan: idle";
        }
    }

    partial void OnDarksOnlyProcessModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDarksOnlyMode));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(CanScanAll));
        OnPropertyChanged(nameof(CanScanFlats));
        RaiseTabWorkflowStateChanged();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnIsGeneratingMatchingChanged(bool value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnPixInsightPathChanged(string value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnUseNativeProcessingChanged(bool value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnOutputRootPathChanged(string value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnMatchingDiagnosticsChanged(ObservableCollection<MatchingDiagnostic> value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnMatchingGroupsChanged(ObservableCollection<MatchingGroupViewModel> value)
    {
        RaiseTabWorkflowStateChanged();
    }

    partial void OnProcessingReportTextChanged(string value)
    {
        RaiseTabWorkflowStateChanged();
    }

    public MainViewModel(
        IFileScannerService fileScanner,
        IDarkMatchingService darkMatcher,
        IPixInsightService pixInsight,
        IImageProcessingEngine nativeEngine,
        IProcessingReportService reportService,
        IMasterDarkMaterializer masterMaterializer,
        ILogger<MainViewModel> logger,
        IConfiguration configuration)
    {
        _fileScanner = fileScanner;
        _darkMatcher = darkMatcher;
        _pixInsight = pixInsight;
        _nativeEngine = nativeEngine;
        _reportService = reportService;
        _logger = logger;
        _masterMaterializer = masterMaterializer;
        _configuration = configuration;

        _pixInsightPath = _configuration["AppSettings:PixInsightExecutable"] ?? "";
        _outputRootPath = _configuration["OutputConfiguration:OutputRootPath"] ?? "D:\\fmOutput";
        _outputFormat = string.Equals(_configuration["OutputConfiguration:OutputFormat"], "FITS", StringComparison.OrdinalIgnoreCase)
            ? "FITS"
            : "XISF";
        _sessionLogPath = BuildSessionLogPath();
        _userSettingsPath = BuildUserSettingsPath();

        if (bool.TryParse(_configuration["ProcessingDefaults:RequireDarks"], out var requireDarks))
            RequireDarks = requireDarks;
        if (bool.TryParse(_configuration["ProcessingDefaults:DarksOnlyProcessMode"], out var darksOnlyMode))
            DarksOnlyProcessMode = darksOnlyMode;
        if (double.TryParse(_configuration["DarkMatching:DarkOverBiasTempDeltaC"], NumberStyles.Any, CultureInfo.InvariantCulture, out var darkOverBias))
            DarkOverBiasTempDeltaC = darkOverBias;
        if (double.TryParse(_configuration["DarkMatching:DarkOverBiasExposureDeltaSeconds"], NumberStyles.Any, CultureInfo.InvariantCulture, out var darkOverBiasExposure))
            DarkOverBiasExposureDeltaSeconds = darkOverBiasExposure;

        var outputMode = _configuration["OutputConfiguration:Mode"] ?? "InlineInSource";
        _useReplicatedOutput = outputMode.Equals("ReplicatedSeparateTree", StringComparison.OrdinalIgnoreCase);

        InitializeUserSettingsPersistence();
        LoadUserSettingsFromDisk();
        RaiseTabWorkflowStateChanged();

        Log($"FlatMaster v1.0.4 - Initialized at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Session log: {_sessionLogPath}");
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
            PixInsightPath = dialog.FileName;
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

    private static string BuildSessionLogPath()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(Path.GetTempPath(), $"FlatMaster_{timestamp}.log");
    }

    private static string BuildUserSettingsPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlatMaster");
        return Path.Combine(root, "user-settings.json");
    }

    private void RaiseTabWorkflowStateChanged()
    {
        OnPropertyChanged(nameof(ConfigurationTabStepBrush));
        OnPropertyChanged(nameof(ScanTabStepBrush));
        OnPropertyChanged(nameof(MatchingTabStepBrush));
        OnPropertyChanged(nameof(ProcessingTabStepBrush));
    }

    private static Brush CreateFrozenBrush(string colorHex)
    {
        var parsed = ColorConverter.ConvertFromString(colorHex);
        if (parsed is not Color color)
            return Brushes.Gray;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [GeneratedRegex(@"Folders (\d+)-(\d+) of (\d+)\s+\((\d+)/(\d+) files\)", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"Folders \d+-\d+ (done|FAILED)\s+\((\d+)/(\d+) files\)", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"Loading\s+(\d+)\s+flat\s+frames", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@"Calibrated\s+(\d+)\s*/\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex3();
    [GeneratedRegex(@"\[OK\]\s+Master\s+flat\s+written", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex4();
    [GeneratedRegex(@"!\s+No\s+dark\/bias.*-\s+skipped", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex5();
    [GeneratedRegex(@"^\s*ERROR:", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MyRegex6();
}


