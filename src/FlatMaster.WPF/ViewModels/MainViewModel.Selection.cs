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

using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Models;

namespace FlatMaster.WPF.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void SelectAllFlats()
    {
        foreach (var dir in FlatDirectories)
            dir.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllFlats()
    {
        foreach (var dir in FlatDirectories)
            dir.IsSelected = false;
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
            foreach (var dark in EnumerateDarkFrameViewModelsRecursive(typeGroup.Children))
                dark.IsSelected = selected;
        }
    }

    private List<DarkFrame> GetSelectedDarks()
    {
        var selected = new List<DarkFrame>();

        foreach (var typeGroup in DarkInventory)
        {
            foreach (var darkVm in EnumerateDarkFrameViewModelsRecursive(typeGroup.Children))
            {
                if (darkVm.IsSelected)
                    selected.Add(darkVm.DarkFrame);
            }
        }

        return selected;
    }

    private static IEnumerable<DarkFrameViewModel> EnumerateDarkFrameViewModelsRecursive(IEnumerable<object> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is DarkFrameViewModel frame)
            {
                yield return frame;
                continue;
            }

            if (node is not DarkGroupViewModel group)
                continue;

            foreach (var nested in EnumerateDarkFrameViewModelsRecursive(group.Children))
                yield return nested;
        }
    }
}

