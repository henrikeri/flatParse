using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;

namespace FlatMaster.Infrastructure.Services;

/// <summary>Checks master presence in mirrored folders without reading image contents or metadata.</summary>
public sealed class FlatVerificationService
{
    public FlatVerificationResult Compare(string sourceRoot, string outputRoot,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(outputRoot))
            throw new DirectoryNotFoundException("Both source and master roots must exist and be accessible.");
        if (Within(sourceRoot, outputRoot) || Within(outputRoot, sourceRoot))
            throw new ArgumentException("Choose separate, non-overlapping source and master trees.");

        var rows = new List<FlatVerificationRow>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        var directories = 0;
        var images = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory);
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked directory was not traversed; compare it separately.");
                var sourcePaths = new List<string>();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsImage(path) && !IsMaster(path)) sourcePaths.Add(path);
                }
                if (sourcePaths.Count > 0)
                    rows.Add(CheckDirectory(outputRoot, relative, sourcePaths, cancellationToken));
                images += sourcePaths.Count;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsWorkingDirectory(Path.GetFileName(child))) pending.Push(child);
                }
                progress?.Report(new ScanProgress { DirectoriesScanned = ++directories, FilesFound = images, CurrentDirectory = directory });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rows.Add(new(relative, 0, 0, FlatVerificationStatus.Failed, "", ex.Message));
            }
        }
        return new(sourceRoot, outputRoot, DateTime.UtcNow, rows.OrderBy(row => row.Status is FlatVerificationStatus.MasterFound or FlatVerificationStatus.CopiesPresent)
            .ThenBy(row => row.RelativeDirectory, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".fits" or ".fit" or ".xisf";
    private static bool IsMaster(string path) => IsImage(path) &&
        Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"^master[ _-]*flat(?:[ _-]|$)", RegexOptions.IgnoreCase);
    private static bool IsWorkingDirectory(string name) => name.Equals(".FlatMaster", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("_CalibratedFlats", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_darkmasters", StringComparison.OrdinalIgnoreCase);

    private static FlatVerificationRow CheckDirectory(string outputRoot, string relative, List<string> sourcePaths, CancellationToken token)
    {
        var sourceCount = sourcePaths.Count;
        var destination = Path.GetFullPath(Path.Combine(outputRoot, relative == "." ? "" : relative));
        try
        {
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Master folder is a link; compare its target separately.");
            var masters = new List<string>();
            var emptyMasters = 0;
            var outputs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(destination))
            {
                token.ThrowIfCancellationRequested();
                if (!IsImage(path)) continue;
                var length = new FileInfo(path).Length;
                outputs[Path.GetFileName(path)] = length;
                if (!IsMaster(path)) continue;
                if (length > 0) masters.Add(path);
                else emptyMasters++;
            }
            masters.Sort(StringComparer.OrdinalIgnoreCase);
            var copies = 0;
            if (masters.Count == 0 && emptyMasters == 0)
            {
                foreach (var source in sourcePaths)
                {
                    token.ThrowIfCancellationRequested();
                    if (outputs.TryGetValue(Path.GetFileName(source), out var length) && length > 0 && new FileInfo(source).Length == length)
                        copies++;
                }
            }
            var status = emptyMasters > 0 ? FlatVerificationStatus.Failed :
                masters.Count > 0 ? FlatVerificationStatus.MasterFound :
                copies == sourceCount ? FlatVerificationStatus.CopiesPresent : FlatVerificationStatus.Missing;
            return new(relative, sourceCount, masters.Count, status,
                status == FlatVerificationStatus.CopiesPresent ? destination : string.Join("; ", masters),
                emptyMasters > 0 ? $"{emptyMasters} empty master file(s). Review this folder." :
                masters.Count > 0 ? "Master presence confirmed. Exposure coverage and image integrity were not checked." :
                copies == sourceCount ? $"All {sourceCount} source copies are present with matching filenames and sizes. File contents were not checked." :
                $"Matching folder exists but contains no master flat; {copies}/{sourceCount} source copies have matching filenames and sizes.");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return new(relative, sourceCount, 0, FlatVerificationStatus.Missing, "", "Matching master folder is missing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(relative, sourceCount, 0, FlatVerificationStatus.Failed, "", ex.Message);
        }
    }

    public static string ExportCsv(FlatVerificationResult result)
    {
        static string Quote(string value)
        {
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("Source root,Master root,Checked UTC,Scope,Relative folder,Source images,Masters found,Status,Master files or copy folder,Detail\r\n");
        foreach (var row in result.Rows)
            csv.AppendLine(string.Join(",", new[] { result.SourceRoot, result.OutputRoot, result.CheckedUtc.ToString("O"), "Folder-level output presence only",
                row.RelativeDirectory, row.SourceCount.ToString(CultureInfo.InvariantCulture), row.MasterCount.ToString(CultureInfo.InvariantCulture), row.StatusText, row.MasterPaths, row.Detail }.Select(Quote)));
        return csv.ToString();
    }
}
