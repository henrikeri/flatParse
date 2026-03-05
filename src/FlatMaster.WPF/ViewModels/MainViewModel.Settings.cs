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

using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    private static readonly JsonSerializerOptions UserSettingsJsonOptions = new()
    {
        WriteIndented = true
    };

    private bool _isApplyingUserSettings;
    private CancellationTokenSource? _userSettingsSaveDebounceCts;
    private readonly HashSet<DirectoryJobViewModel> _trackedFlatDirectoryViewModels = [];
    private readonly HashSet<DarkFrameViewModel> _trackedDarkFrameViewModels = [];
    private HashSet<string> _savedSelectedFlatDirectories = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _savedDeselectedDarkFiles = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeUserSettingsPersistence()
    {
        PropertyChanged += OnMainViewModelPropertyChangedForSettings;
        FlatBaseRoots.CollectionChanged += OnRootDirectoriesChanged;
        DarkLibraryRoots.CollectionChanged += OnRootDirectoriesChanged;
    }

    private void LoadUserSettingsFromDisk()
    {
        if (!File.Exists(_userSettingsPath))
            return;

        try
        {
            var json = File.ReadAllText(_userSettingsPath);
            var settings = JsonSerializer.Deserialize<UserUiSettings>(json);
            if (settings == null)
                return;

            _isApplyingUserSettings = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.PixInsightPath))
                    PixInsightPath = settings.PixInsightPath;
                if (!string.IsNullOrWhiteSpace(settings.OutputRootPath))
                    OutputRootPath = settings.OutputRootPath;
                if (!string.IsNullOrWhiteSpace(settings.OutputFormat))
                    OutputFormat = string.Equals(settings.OutputFormat, "FITS", StringComparison.OrdinalIgnoreCase) ? "FITS" : "XISF";

                if (settings.UseNativeProcessing.HasValue)
                    UseNativeProcessing = settings.UseNativeProcessing.Value;
                if (settings.DeleteCalibrated.HasValue)
                    DeleteCalibrated = settings.DeleteCalibrated.Value;
                if (settings.RequireDarks.HasValue)
                    RequireDarks = settings.RequireDarks.Value;
                if (settings.DarksOnlyProcessMode.HasValue)
                    DarksOnlyProcessMode = settings.DarksOnlyProcessMode.Value;
                if (settings.UseReplicatedOutput.HasValue)
                    UseReplicatedOutput = settings.UseReplicatedOutput.Value;
                if (settings.CopyOnlyProcessed.HasValue)
                    CopyOnlyProcessed = settings.CopyOnlyProcessed.Value;
                if (settings.DarkOverBiasTempDeltaC.HasValue)
                    DarkOverBiasTempDeltaC = settings.DarkOverBiasTempDeltaC.Value;
                if (settings.DarkOverBiasExposureDeltaSeconds.HasValue)
                    DarkOverBiasExposureDeltaSeconds = settings.DarkOverBiasExposureDeltaSeconds.Value;

                ReplaceCollection(FlatBaseRoots, settings.FlatBaseRoots);
                ReplaceCollection(DarkLibraryRoots, settings.DarkLibraryRoots);

                _savedSelectedFlatDirectories = NormalizeDistinctPaths(settings.SelectedFlatDirectories)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _savedDeselectedDarkFiles = NormalizeDistinctPaths(settings.DeselectedDarkFilePaths)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _isApplyingUserSettings = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading user settings from {Path}", _userSettingsPath);
        }
    }

    private void OnMainViewModelPropertyChangedForSettings(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingUserSettings)
            return;

        if (e.PropertyName is nameof(PixInsightPath)
            or nameof(OutputRootPath)
            or nameof(OutputFormat)
            or nameof(UseNativeProcessing)
            or nameof(DeleteCalibrated)
            or nameof(RequireDarks)
            or nameof(DarksOnlyProcessMode)
            or nameof(UseReplicatedOutput)
            or nameof(CopyOnlyProcessed)
            or nameof(DarkOverBiasTempDeltaC)
            or nameof(DarkOverBiasExposureDeltaSeconds))
        {
            ScheduleUserSettingsSave();
        }
    }

    private void OnRootDirectoriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isApplyingUserSettings)
            return;
        RaiseTabWorkflowStateChanged();
        ScheduleUserSettingsSave();
    }

    private void ScheduleUserSettingsSave()
    {
        if (_isApplyingUserSettings)
            return;

        _userSettingsSaveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _userSettingsSaveDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                SaveUserSettingsToDisk();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed saving user settings");
            }
        }, token);
    }

    private void SaveUserSettingsToDisk()
    {
        if (_isApplyingUserSettings)
            return;

        RefreshCachedSelectionSets();

        var settings = new UserUiSettings
        {
            PixInsightPath = PixInsightPath,
            OutputRootPath = OutputRootPath,
            OutputFormat = OutputFormat,
            UseNativeProcessing = UseNativeProcessing,
            DeleteCalibrated = DeleteCalibrated,
            RequireDarks = RequireDarks,
            DarksOnlyProcessMode = DarksOnlyProcessMode,
            UseReplicatedOutput = UseReplicatedOutput,
            CopyOnlyProcessed = CopyOnlyProcessed,
            DarkOverBiasTempDeltaC = DarkOverBiasTempDeltaC,
            DarkOverBiasExposureDeltaSeconds = DarkOverBiasExposureDeltaSeconds,
            FlatBaseRoots = NormalizeDistinctPaths(FlatBaseRoots).ToList(),
            DarkLibraryRoots = NormalizeDistinctPaths(DarkLibraryRoots).ToList(),
            SelectedFlatDirectories = _savedSelectedFlatDirectories
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DeselectedDarkFilePaths = _savedDeselectedDarkFiles
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var directory = Path.GetDirectoryName(_userSettingsPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        var tmpPath = _userSettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, UserSettingsJsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Copy(tmpPath, _userSettingsPath, true);
        File.Delete(tmpPath);
    }

    private void ReplaceCollection(ObservableCollection<string> target, IEnumerable<string>? paths)
    {
        target.Clear();
        if (paths == null)
            return;

        foreach (var path in NormalizeDistinctPaths(paths))
            target.Add(path);
    }

    private static IEnumerable<string> NormalizeDistinctPaths(IEnumerable<string> paths)
    {
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePathForStorage)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePathForStorage(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private void RegisterFlatDirectorySelectionTracking()
    {
        UnregisterFlatDirectorySelectionTracking();
        foreach (var vm in FlatDirectories)
        {
            if (_trackedFlatDirectoryViewModels.Add(vm))
                vm.PropertyChanged += OnFlatDirectoryPropertyChangedForSettings;
        }

        ApplySavedFlatSelections();
        RefreshCachedFlatSelections();
    }

    private void UnregisterFlatDirectorySelectionTracking()
    {
        foreach (var vm in _trackedFlatDirectoryViewModels)
            vm.PropertyChanged -= OnFlatDirectoryPropertyChangedForSettings;
        _trackedFlatDirectoryViewModels.Clear();
    }

    private void OnFlatDirectoryPropertyChangedForSettings(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingUserSettings || e.PropertyName != nameof(DirectoryJobViewModel.IsSelected))
            return;

        RefreshCachedFlatSelections();
        ScheduleUserSettingsSave();
    }

    private void ApplySavedFlatSelections()
    {
        if (_savedSelectedFlatDirectories.Count == 0)
            return;

        _isApplyingUserSettings = true;
        try
        {
            foreach (var vm in FlatDirectories)
            {
                var key = NormalizePathForStorage(vm.DirectoryPath);
                vm.IsSelected = _savedSelectedFlatDirectories.Contains(key);
            }
        }
        finally
        {
            _isApplyingUserSettings = false;
        }
    }

    private void RegisterDarkSelectionTracking()
    {
        UnregisterDarkSelectionTracking();
        foreach (var frameVm in EnumerateDarkFrameViewModels())
        {
            if (_trackedDarkFrameViewModels.Add(frameVm))
                frameVm.PropertyChanged += OnDarkFramePropertyChangedForSettings;
        }

        ApplySavedDarkSelections();
        RefreshCachedDarkSelections();
    }

    private void UnregisterDarkSelectionTracking()
    {
        foreach (var vm in _trackedDarkFrameViewModels)
            vm.PropertyChanged -= OnDarkFramePropertyChangedForSettings;
        _trackedDarkFrameViewModels.Clear();
    }

    private void OnDarkFramePropertyChangedForSettings(object? sender, PropertyChangedEventArgs e)
    {
        if (_isApplyingUserSettings || e.PropertyName != nameof(DarkFrameViewModel.IsSelected))
            return;

        RefreshCachedDarkSelections();
        ScheduleUserSettingsSave();
    }

    private void ApplySavedDarkSelections()
    {
        if (_savedDeselectedDarkFiles.Count == 0)
            return;

        _isApplyingUserSettings = true;
        try
        {
            foreach (var vm in _trackedDarkFrameViewModels)
            {
                var key = NormalizePathForStorage(vm.DarkFrame.FilePath);
                vm.IsSelected = !_savedDeselectedDarkFiles.Contains(key);
            }
        }
        finally
        {
            _isApplyingUserSettings = false;
        }
    }

    private IEnumerable<DarkFrameViewModel> EnumerateDarkFrameViewModels()
    {
        foreach (var typeGroup in DarkInventory)
        {
            foreach (var frameVm in EnumerateDarkFrameViewModelsRecursive(typeGroup.Children))
                yield return frameVm;
        }
    }

    private void RefreshCachedSelectionSets()
    {
        RefreshCachedFlatSelections();
        RefreshCachedDarkSelections();
    }

    private void RefreshCachedFlatSelections()
    {
        if (_trackedFlatDirectoryViewModels.Count == 0)
            return;

        _savedSelectedFlatDirectories = _trackedFlatDirectoryViewModels
            .Where(vm => vm.IsSelected)
            .Select(vm => NormalizePathForStorage(vm.DirectoryPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshCachedDarkSelections()
    {
        if (_trackedDarkFrameViewModels.Count == 0)
            return;

        _savedDeselectedDarkFiles = _trackedDarkFrameViewModels
            .Where(vm => !vm.IsSelected)
            .Select(vm => NormalizePathForStorage(vm.DarkFrame.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UserUiSettings
    {
        public string? PixInsightPath { get; init; }
        public string? OutputRootPath { get; init; }
        public string? OutputFormat { get; init; }
        public bool? UseNativeProcessing { get; init; }
        public bool? DeleteCalibrated { get; init; }
        public bool? RequireDarks { get; init; }
        public bool? DarksOnlyProcessMode { get; init; }
        public bool? UseReplicatedOutput { get; init; }
        public bool? CopyOnlyProcessed { get; init; }
        public double? DarkOverBiasTempDeltaC { get; init; }
        public double? DarkOverBiasExposureDeltaSeconds { get; init; }
        public List<string> FlatBaseRoots { get; init; } = [];
        public List<string> DarkLibraryRoots { get; init; } = [];
        public List<string> SelectedFlatDirectories { get; init; } = [];
        public List<string> DeselectedDarkFilePaths { get; init; } = [];
    }
}
