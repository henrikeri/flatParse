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

public partial class DarkGroupViewModel(string name, DarkGroupViewModel? parent = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public string Name { get; } = name;
    public ObservableCollection<object> Children { get; } = [];
    public DarkGroupViewModel? Parent { get; } = parent;
    private bool _isSyncingSelection;

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

            if (isChildSelected)
                continue;

            allSelected = false;
            break;
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

