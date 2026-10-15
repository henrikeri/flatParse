using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;

namespace FlatMaster.WPF.ViewModels;

public partial class FlatVerificationViewModel : ObservableObject
{
    [ObservableProperty] private string _sourceRoot = "";
    [ObservableProperty] private string _masterRoot = "";
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _summary = "Choose the corresponding source and master roots, then compare.";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _conclusion = "";
    [ObservableProperty] private string _folderOpenStatus = "";
    [ObservableProperty] private ObservableCollection<FlatVerificationRow> _rows = [];
    private FlatVerificationResult? _result;
    public bool IsIdle => !IsComparing;

    partial void OnIsComparingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnSourceRootChanged(string value) => Invalidate();
    partial void OnMasterRootChanged(string value) => Invalidate();
    private void Invalidate()
    {
        _result = null;
        Rows.Clear();
        ProgressText = "";
        Conclusion = "";
        FolderOpenStatus = "";
        Summary = "Folders changed. Compare to verify these trees.";
        ExportReportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the unprocessed source root" };
        if (dialog.ShowDialog() == true) SourceRoot = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseMasters()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the corresponding master output root" };
        if (dialog.ShowDialog() == true) MasterRoot = dialog.FolderName;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CompareAsync(CancellationToken token)
    {
        var source = SourceRoot;
        var output = MasterRoot;
        Invalidate();
        IsComparing = true;
        Summary = "Comparing folders…";
        var progress = new Progress<ScanProgress>(p => ProgressText = $"{p.DirectoriesScanned} folders · {p.FilesFound} images · {p.CurrentDirectory}");
        try
        {
            var result = await Task.Run(() => new FlatVerificationService().Compare(source, output, progress, token), token);
            // Paths can also change programmatically during a comparison.
            if (source != SourceRoot || output != MasterRoot) { Invalidate(); return; }
            _result = result;
            Rows = new(result.Rows);
            Summary = result.Summary;
            Conclusion = result.AllOutputsPresent
                ? $"All source image folders contain masters or matching source copies. Checked {result.CheckedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}."
                : "Presence check incomplete. Resolve missing or failed folders before removing source images.";
            ProgressText = "Folder-level presence only; exposure coverage and image integrity were not checked. Compare again if either tree changes.";
        }
        catch (OperationCanceledException) { Summary = "Comparison cancelled; no complete verification result."; ProgressText = ""; }
        catch (Exception ex) { Summary = "Comparison failed: " + ex.Message; ProgressText = ""; }
        finally { IsComparing = false; ExportReportCommand.NotifyCanExecuteChanged(); }
    }

    public void OpenFolders(FlatVerificationRow row)
    {
        if (_result == null || IsComparing || !Rows.Contains(row)) return;
        var notices = new List<string>();
        foreach (var (label, root) in new[] { ("Source", _result.SourceRoot), ("Master", _result.OutputRoot) })
        {
            try
            {
                var expected = Path.GetFullPath(Path.Combine(root, row.RelativeDirectory == "." ? "" : row.RelativeDirectory));
                var folder = FindExistingFolder(expected, root);
                if (folder == null) { notices.Add($"{label} folder is unavailable: {expected}"); continue; }
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                if (!string.Equals(folder, expected, StringComparison.OrdinalIgnoreCase))
                    notices.Add($"{label} folder is missing; opened its nearest existing parent: {folder}");
            }
            catch (Exception ex) { notices.Add($"Could not open {label.ToLowerInvariant()} folder: {ex.Message}"); }
        }
        FolderOpenStatus = notices.Count == 0 ? "Opened source and master folders." : string.Join("  ", notices);
    }

    private static string? FindExistingFolder(string expected, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        for (string? candidate = expected; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            if (Directory.Exists(candidate)) return candidate;
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return null;
    }

    private bool CanExportReport() => _result != null && !IsComparing;
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        if (_result == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save verification report", Filter = "CSV report (*.csv)|*.csv", FileName = "FlatMaster-verification.csv" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, FlatVerificationService.ExportCsv(_result), new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Cannot save report"); }
    }
}
