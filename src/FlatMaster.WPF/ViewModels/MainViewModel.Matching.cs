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
using FlatMaster.Core.Models;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    private void SyncGroupOverrideSelectionsFromUi()
    {
        _groupOverrideSelections.Clear();
        _groupIncludeSelections.Clear();
        foreach (var group in MatchingGroups)
        {
            var path = group.SelectedOverride?.DarkPath;
            if (!string.IsNullOrWhiteSpace(path))
                _groupOverrideSelections[group.GroupKey] = path;

            _groupIncludeSelections[group.GroupKey] = group.IsIncluded;
        }
    }

    private List<DirectoryJob> BuildJobsWithGroupOverrides(IEnumerable<DirectoryJob> jobs, bool applyIncludeSelections = true)
    {
        var overrides = new Dictionary<string, string?>(_groupOverrideSelections, StringComparer.OrdinalIgnoreCase);
        var includes = new Dictionary<string, bool>(_groupIncludeSelections, StringComparer.OrdinalIgnoreCase);
        return BuildJobsWithGroupOverrides(jobs, overrides, includes, applyIncludeSelections);
    }

    private static List<DirectoryJob> BuildJobsWithGroupOverrides(
        IEnumerable<DirectoryJob> jobs,
        IReadOnlyDictionary<string, string?> overrideSelections,
        IReadOnlyDictionary<string, bool> includeSelections,
        bool applyIncludeSelections)
    {
        var result = new List<DirectoryJob>();
        foreach (var job in jobs)
        {
            var mappedGroups = new List<ExposureGroup>();
            foreach (var group in job.ExposureGroups)
            {
                var key = BuildGroupKey(job, group);
                if (applyIncludeSelections && includeSelections.TryGetValue(key, out var include) && !include)
                    continue;

                overrideSelections.TryGetValue(key, out var manualPath);
                var criteria = (group.MatchingCriteria ?? new MatchingCriteria()) with
                {
                    ManualDarkPath = manualPath
                };

                mappedGroups.Add(new ExposureGroup
                {
                    ExposureTime = group.ExposureTime,
                    FilePaths = [.. group.FilePaths],
                    RepresentativeMetadata = group.RepresentativeMetadata,
                    AverageTemperatureC = group.AverageTemperatureC,
                    MatchingCriteria = criteria
                });
            }

            if (mappedGroups.Count == 0)
                continue;

            result.Add(new DirectoryJob
            {
                DirectoryPath = job.DirectoryPath,
                BaseRootPath = job.BaseRootPath,
                OutputRootPath = job.OutputRootPath,
                RelativeDirectory = job.RelativeDirectory,
                ExposureGroups = mappedGroups,
                IsSelected = job.IsSelected
            });
        }

        return result;
    }

    private async Task<List<DirectoryJob>> BuildJobsWithGroupOverridesAsync(
        IEnumerable<DirectoryJob> jobs,
        bool applyIncludeSelections = true,
        CancellationToken cancellationToken = default)
    {
        var jobSnapshot = jobs.ToList();
        var overrideSelections = new Dictionary<string, string?>(_groupOverrideSelections, StringComparer.OrdinalIgnoreCase);
        var includeSelections = new Dictionary<string, bool>(_groupIncludeSelections, StringComparer.OrdinalIgnoreCase);
        return await Task.Run(
            () => BuildJobsWithGroupOverrides(jobSnapshot, overrideSelections, includeSelections, applyIncludeSelections),
            cancellationToken);
    }

    private Task<List<DirectoryJob>> BuildJobsWithGroupOverridesAsync(
        IEnumerable<DirectoryJob> jobs,
        CancellationToken cancellationToken)
    {
        return BuildJobsWithGroupOverridesAsync(jobs, applyIncludeSelections: true, cancellationToken);
    }

    private async Task BuildMatchingGroupsAsync(
        IEnumerable<DirectoryJob> selectedJobs,
        List<DarkFrame> selectedDarks,
        IEnumerable<MatchingDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        var overrideSelections = new Dictionary<string, string?>(_groupOverrideSelections, StringComparer.OrdinalIgnoreCase);
        var includeSelections = new Dictionary<string, bool>(_groupIncludeSelections, StringComparer.OrdinalIgnoreCase);
        var groups = await Task.Run(
            () => BuildMatchingGroupsCore(selectedJobs, selectedDarks, diagnostics, overrideSelections, includeSelections, cancellationToken),
            cancellationToken);

        MatchingGroups = new ObservableCollection<MatchingGroupViewModel>(groups);
    }

    private static List<MatchingGroupViewModel> BuildMatchingGroupsCore(
        IEnumerable<DirectoryJob> selectedJobs,
        List<DarkFrame> selectedDarks,
        IEnumerable<MatchingDiagnostic> diagnostics,
        IReadOnlyDictionary<string, string?> groupOverrideSelections,
        IReadOnlyDictionary<string, bool> groupIncludeSelections,
        CancellationToken cancellationToken)
    {
        var diagnosticsByPath = diagnostics
            .GroupBy(d => d.FlatPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var groups = new List<MatchingGroupViewModel>();
        foreach (var job in selectedJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var group in job.ExposureGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = BuildGroupKey(job, group);
                var groupDiagnostics = group.FilePaths
                    .Select(p => diagnosticsByPath.TryGetValue(p, out var d) ? d : null)
                    .Where(d => d != null)
                    .Cast<MatchingDiagnostic>()
                    .ToList();
                if (groupDiagnostics.Count == 0)
                    continue;

                var darkPaths = groupDiagnostics
                    .Select(d => d.SelectedDark?.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var reasons = groupDiagnostics
                    .Select(d => d.SelectionReason)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var tempDeltas = groupDiagnostics
                    .Where(d => d.TemperatureDeltaC.HasValue)
                    .Select(d => d.TemperatureDeltaC!.Value)
                    .ToList();

                var overrideOptions = new ObservableCollection<DarkOverrideOptionViewModel>(BuildOverrideOptions(group, selectedDarks));
                groupOverrideSelections.TryGetValue(key, out var selectedOverridePath);
                var selectedOverride = overrideOptions.FirstOrDefault(o =>
                    string.Equals(o.DarkPath, selectedOverridePath, StringComparison.OrdinalIgnoreCase)) ?? overrideOptions.FirstOrDefault();

                var selectedDarkDisplay = darkPaths.Count switch
                {
                    0 => "None",
                    1 => Path.GetFileName(darkPaths[0]),
                    _ => $"Mixed ({darkPaths.Count})"
                };

                var groupAverageTempC = group.AverageTemperatureC ?? group.MatchingCriteria?.Temperature;
                double? groupDeltaC = null;
                var tempDisplay = "Not available";
                if (groupAverageTempC.HasValue && darkPaths.Count == 1)
                {
                    var representativeDiag = groupDiagnostics
                        .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.SelectedDark?.FilePath));
                    var darkTempC = ResolveGroupDarkTemperatureForDisplay(
                        representativeDiag,
                        reasons,
                        selectedDarks,
                        group.ExposureTime);

                    if (darkTempC.HasValue)
                    {
                        var flatRoundedC = RoundTempToDisplay(groupAverageTempC.Value);
                        var darkRoundedC = RoundTempToDisplay(darkTempC.Value);
                        groupDeltaC = Math.Abs(flatRoundedC - darkRoundedC);
                        tempDisplay = FormatTempC(groupDeltaC.Value);
                    }
                }
                else if (darkPaths.Count > 1)
                {
                    tempDisplay = "Mixed";
                }
                else if (tempDeltas.Count > 0)
                {
                    // Fallback when dark metadata temperature is unavailable.
                    groupDeltaC = tempDeltas.Average();
                    tempDisplay = groupDeltaC.Value.ToString("F1", CultureInfo.InvariantCulture) + " degrees C";
                }

                var confidence = groupDiagnostics.Average(d => d.ConfidenceScore);
                var hasMinimumFrames = group.FilePaths.Count >= 3;
                var selectionReason = reasons.Count == 1 ? reasons[0] : $"Mixed ({reasons.Count})";
                if (!hasMinimumFrames)
                    selectionReason = "[<3 flats] " + selectionReason;

                groups.Add(new MatchingGroupViewModel
                {
                    GroupKey = key,
                    DirectoryPath = job.DirectoryPath,
                    ExposureTime = group.ExposureTime,
                    FlatAverageTemperatureSortValue = groupAverageTempC ?? double.PositiveInfinity,
                    FlatAverageTemperatureDisplay = group.AverageTemperatureC.HasValue
                        ? FormatTempC(group.AverageTemperatureC.Value)
                        : "Not available",
                    FileCount = group.FilePaths.Count,
                    HasMinimumFrames = hasMinimumFrames,
                    SelectedDarkDisplay = selectedDarkDisplay,
                    SelectionReason = selectionReason,
                    TemperatureDeltaSortValue = groupDeltaC ?? double.PositiveInfinity,
                    TemperatureDeltaDisplay = tempDisplay,
                    ConfidenceSortValue = confidence,
                    ConfidenceDisplay = (confidence * 100).ToString("F0", CultureInfo.InvariantCulture) + "%",
                    FlatFiles = new ObservableCollection<string>(
                        group.FilePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)),
                    OverrideOptions = overrideOptions,
                    IsIncluded = groupIncludeSelections.TryGetValue(key, out var includeSelection)
                        ? includeSelection
                        : hasMinimumFrames,
                    SelectedOverride = selectedOverride
                });
            }
        }

        return groups
            .OrderBy(g => g.DirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.ExposureTime)
            .ToList();
    }

    private static List<DarkOverrideOptionViewModel> BuildOverrideOptions(ExposureGroup group, IEnumerable<DarkFrame> selectedDarks)
    {
        var options = new List<DarkOverrideOptionViewModel>
        {
            new() { DisplayName = "Auto (recommended)", DarkPath = null }
        };

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exposure = group.ExposureTime;
        var potential = selectedDarks
            .Where(d => d.Type is ImageType.MasterDark or ImageType.MasterDarkFlat or ImageType.Dark or ImageType.DarkFlat)
            .Where(d => Math.Abs(d.ExposureTime - exposure) <= 10.0)
            .ToList();

        foreach (var master in potential
                     .Where(d => d.Type is ImageType.MasterDark or ImageType.MasterDarkFlat)
                     .OrderBy(d => Math.Abs(d.ExposureTime - exposure))
                     .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!seenPaths.Add(master.FilePath))
                continue;

            options.Add(new DarkOverrideOptionViewModel
            {
                DarkPath = master.FilePath,
                DisplayName = string.Format(
                    CultureInfo.InvariantCulture,
                    "Master: {0} ({1:0.###} seconds, {2})",
                    Path.GetFileName(master.FilePath),
                    master.ExposureTime,
                    FormatTemperature(master.Temperature))
            });
        }

        var looseGroups = potential
            .Where(d => d.Type is ImageType.Dark or ImageType.DarkFlat)
            .GroupBy(d => new
            {
                Folder = d.DarkGroupFolder ?? Path.GetDirectoryName(d.FilePath) ?? "DARKS",
                Exposure = Math.Round(d.ExposureTime, 3, MidpointRounding.AwayFromZero),
                Temp = d.Temperature.HasValue ? Math.Round(d.Temperature.Value, 1, MidpointRounding.AwayFromZero) : (double?)null
            })
            .OrderBy(g => Math.Abs(g.Key.Exposure - exposure))
            .ThenBy(g => g.Key.Folder, StringComparer.OrdinalIgnoreCase);

        foreach (var loose in looseGroups)
        {
            var representative = loose.OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase).First();
            if (!seenPaths.Add(representative.FilePath))
                continue;

            var folderName = Path.GetFileName(loose.Key.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            options.Add(new DarkOverrideOptionViewModel
            {
                DarkPath = representative.FilePath,
                DisplayName = string.Format(
                    CultureInfo.InvariantCulture,
                    "Loose->Master: {0} ({1} frames, {2:0.###} seconds, {3})",
                    string.IsNullOrWhiteSpace(folderName) ? loose.Key.Folder : folderName,
                    loose.Count(),
                    loose.Key.Exposure,
                    FormatTemperature(loose.Key.Temp))
            });
        }

        return options;
    }

    private static string BuildGroupKey(DirectoryJob job, ExposureGroup group)
    {
        var firstFile = group.FilePaths
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2}",
            Path.GetFullPath(job.DirectoryPath),
            group.ExposureTime.ToString("R", CultureInfo.InvariantCulture),
            firstFile);
    }

    private static string FormatTemperature(double? temperatureC)
    {
        return temperatureC.HasValue
            ? temperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture) + " degrees C"
            : "unknown temperature";
    }

    private static double? ResolveGroupDarkTemperatureForDisplay(
        MatchingDiagnostic? representativeDiag,
        List<string> reasons,
        List<DarkFrame> selectedDarks,
        double _)
    {
        if (representativeDiag?.SelectedDark == null)
            return null;

        var selectedDark = representativeDiag.SelectedDark;
        var reason = reasons.Count == 1 ? reasons[0] : string.Empty;

        // For raw-dark priorities (P2/P4/P6), use the average temperature of all
        // raw dark frames in the same DARKSxxx folder at the matched dark exposure.
        if ((reason.StartsWith("P2 ", StringComparison.OrdinalIgnoreCase)
             || reason.StartsWith("P4 ", StringComparison.OrdinalIgnoreCase)
             || reason.StartsWith("P6 ", StringComparison.OrdinalIgnoreCase))
            && selectedDark.Type is ImageType.Dark or ImageType.DarkFlat)
        {
            var selectedGroupFolder = selectedDark.DarkGroupFolder
                ?? Path.GetDirectoryName(selectedDark.FilePath)
                ?? string.Empty;
            var exactTemps = selectedDarks
                .Where(d => d.Type is ImageType.Dark or ImageType.DarkFlat)
                .Where(d => string.Equals(
                    d.DarkGroupFolder ?? Path.GetDirectoryName(d.FilePath) ?? string.Empty,
                    selectedGroupFolder,
                    StringComparison.OrdinalIgnoreCase))
                .Where(d => Math.Abs(d.ExposureTime - selectedDark.ExposureTime) < 0.001)
                .Where(d => d.Temperature.HasValue)
                .Select(d => d.Temperature!.Value)
                .ToList();

            if (exactTemps.Count > 0)
                return exactTemps.Average();
        }

        return selectedDark.Temperature;
    }

    private static string FormatTempC(double valueC)
    {
        var rounded = Math.Round(valueC, 1, MidpointRounding.AwayFromZero);
        return rounded.ToString("F1", CultureInfo.InvariantCulture) + " degrees C";
    }

    private static double RoundTempToDisplay(double valueC)
    {
        return Math.Round(valueC, 1, MidpointRounding.AwayFromZero);
    }
}
