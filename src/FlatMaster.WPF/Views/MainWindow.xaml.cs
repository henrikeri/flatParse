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
using System.Reflection;
using System.Windows;
using FlatMaster.WPF.ViewModels;

namespace FlatMaster.WPF.Views;

public partial class MainWindow : Window
{
    private const string LicenseResourceName = "FlatMaster.WPF.LICENSE.txt";
    private const string ThirdPartyResourceName = "FlatMaster.WPF.THIRD-PARTY-NOTICES.txt";

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnLicensesClick(object sender, RoutedEventArgs e)
    {
        var licensePath = ExtractEmbeddedNoticeToTemp(LicenseResourceName, "LICENSE.txt")
            ?? ResolveLooseNoticeFilePath("LICENSE");
        var noticesPath = ExtractEmbeddedNoticeToTemp(ThirdPartyResourceName, "THIRD-PARTY-NOTICES.txt")
            ?? ResolveLooseNoticeFilePath("THIRD-PARTY-NOTICES");

        var message = "This application is licensed under the GNU GPLv3.\n\n" +
            "Open license files now?";
        var result = MessageBox.Show(message, "Licenses", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (result == MessageBoxResult.OK)
        {
            OpenNoticeFile(licensePath, "LICENSE");
            OpenNoticeFile(noticesPath, "THIRD-PARTY-NOTICES");
        }
    }

    private static string? ExtractEmbeddedNoticeToTemp(string resourceName, string targetFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlatMaster",
            "Licenses");
        Directory.CreateDirectory(tempDir);
        var targetPath = Path.Combine(tempDir, targetFileName);

        using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.CopyTo(outStream);
        return targetPath;
    }

    private static string? ResolveLooseNoticeFilePath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, fileName);
        if (File.Exists(candidate))
            return candidate;

        // Fallback for unusual launch layouts.
        var parent = Directory.GetParent(baseDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidate = Path.Combine(parent, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void OpenNoticeFile(string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(
                $"Could not open {fileName} from embedded resources or local files.",
                "Licenses",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

