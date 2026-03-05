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
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlatMaster.WPF.ViewModels;

public partial class MatchingGroupViewModel : ObservableObject
{
    public required string GroupKey { get; init; }
    public required string DirectoryPath { get; init; }
    public required double ExposureTime { get; init; }
    public required double FlatAverageTemperatureSortValue { get; init; }
    public required string FlatAverageTemperatureDisplay { get; init; }
    public required int FileCount { get; init; }
    public required bool HasMinimumFrames { get; init; }
    public required string SelectedDarkDisplay { get; init; }
    public required string SelectionReason { get; init; }
    public required double TemperatureDeltaSortValue { get; init; }
    public required string TemperatureDeltaDisplay { get; init; }
    public required double ConfidenceSortValue { get; init; }
    public required string ConfidenceDisplay { get; init; }
    public required ObservableCollection<string> FlatFiles { get; init; }
    public required ObservableCollection<DarkOverrideOptionViewModel> OverrideOptions { get; init; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isIncluded = true;

    [ObservableProperty]
    private DarkOverrideOptionViewModel? _selectedOverride;
}

public sealed class DarkOverrideOptionViewModel
{
    public required string DisplayName { get; init; }
    public string? DarkPath { get; init; }
}
