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

using CommunityToolkit.Mvvm.ComponentModel;
using FlatMaster.Core.Models;
using System.IO;

namespace FlatMaster.WPF.ViewModels;

public partial class DarkFrameViewModel(DarkFrame darkFrame, DarkGroupViewModel parent) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public DarkFrame DarkFrame { get; } = darkFrame;
    public DarkGroupViewModel Parent { get; } = parent;
    public string FileName => Path.GetFileName(DarkFrame.FilePath);
    private bool _isSyncingSelection;

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

