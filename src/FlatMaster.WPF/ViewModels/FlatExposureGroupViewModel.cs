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

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using FlatMaster.Core.Models;

namespace FlatMaster.WPF.ViewModels;

public sealed class FlatExposureGroupViewModel(ExposureGroup group)
{
    public double ExposureTime { get; } = group.ExposureTime;
    public double? AverageTemperatureC { get; } = group.AverageTemperatureC;
    public int FileCount { get; } = group.FilePaths.Count;
    public ObservableCollection<FlatFileViewModel> Files { get; } = new(
        group.FilePaths
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new FlatFileViewModel(p)));

    public string ExposureDisplay => string.Format(CultureInfo.InvariantCulture, "{0:0.000}s", ExposureTime);

    public string AverageTemperatureDisplay => AverageTemperatureC.HasValue
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} degC", AverageTemperatureC.Value)
        : "N/A";

    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "Exposure {0}, Avg Temp {1}, Files {2}",
        ExposureDisplay,
        AverageTemperatureDisplay,
        FileCount);
}

public sealed class FlatFileViewModel(string fullPath)
{
    public string FullPath { get; } = fullPath;
    public string DisplayName { get; } = Path.GetFileName(fullPath);
}
