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

using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FlatMaster.Core.Models;

namespace FlatMaster.WPF.ViewModels;

public partial class DirectoryJobViewModel(DirectoryJob job) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private bool _isExpanded;

    public string DirectoryPath { get; } = job.DirectoryPath;
    public string DisplayPath { get; } = job.RelativeDirectory ?? Path.GetFileName(job.DirectoryPath);
    public int GroupCount { get; } = job.ValidGroupCount;
    public int FileCount { get; } = job.TotalFileCount;
    public ObservableCollection<FlatExposureGroupViewModel> ExposureGroups { get; } = new(
        job.ExposureGroups
            .OrderBy(g => g.ExposureTime)
            .Select(g => new FlatExposureGroupViewModel(g)));
}

