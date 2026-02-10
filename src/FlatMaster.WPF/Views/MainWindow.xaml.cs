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
using System.Diagnostics;
using System.IO;
using System.Windows;
using FlatMaster.WPF.ViewModels;

namespace FlatMaster.WPF.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnLicensesClick(object sender, RoutedEventArgs e)
    {
        var baseDir = AppContext.BaseDirectory;
        var licensePath = Path.Combine(baseDir, "LICENSE");
        var noticesPath = Path.Combine(baseDir, "THIRD-PARTY-NOTICES");

        var message = "This application is licensed under the GNU GPLv3.\n\n" +
            "License file:\n" + licensePath + "\n\n" +
            "Third-party notices:\n" + noticesPath + "\n\n" +
            "Open these files?";
        var result = MessageBox.Show(message, "Licenses", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (result == MessageBoxResult.OK)
        {
            OpenNoticeFile(licensePath);
            OpenNoticeFile(noticesPath);
        }
    }

    private static void OpenNoticeFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"File not found:\n{path}", "Licenses", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

