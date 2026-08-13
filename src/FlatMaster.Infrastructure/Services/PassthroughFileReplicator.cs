// Copyright (C) 2026 Henrik E. Riise
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FlatMaster.Core.Models;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Replicates preservation-only image files without calibration or conversion.
/// </summary>
public static class PassthroughFileReplicator
{
    public static async Task<List<string>> CopyAsync(
        IEnumerable<DirectoryJob> jobs,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var copied = new List<string>();

        foreach (var job in jobs)
        {
            foreach (var sourcePath in job.PassthroughFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException("Single-image preservation source was not found.", sourcePath);

                var sourceFullPath = Path.GetFullPath(sourcePath);
                var expectedSourceDirectory = Path.GetFullPath(job.DirectoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var actualSourceDirectory = (Path.GetDirectoryName(sourceFullPath) ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(expectedSourceDirectory, actualSourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Refusing to preserve a file outside its source directory: {sourceFullPath}");
                }

                var outputRoot = Path.GetFullPath(job.OutputRootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var relativeDirectory = string.IsNullOrWhiteSpace(job.RelativeDirectory) || job.RelativeDirectory == "."
                    ? string.Empty
                    : job.RelativeDirectory;
                var destinationDirectory = Path.GetFullPath(Path.Combine(outputRoot, relativeDirectory))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var outputRootPrefix = outputRoot + Path.DirectorySeparatorChar;
                if (!string.Equals(destinationDirectory, outputRoot, StringComparison.OrdinalIgnoreCase) &&
                    !destinationDirectory.StartsWith(outputRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Refusing to preserve a file outside the output root: {destinationDirectory}");
                }

                Directory.CreateDirectory(destinationDirectory);
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFullPath));
                if (string.Equals(sourceFullPath, Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    copied.Add(destinationPath);
                    continue;
                }

                var temporaryPath = destinationPath + ".flatmaster_copy_" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var source = new FileStream(
                                     sourceFullPath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.Read,
                                     bufferSize: 1024 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var destination = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     bufferSize: 1024 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
                        await destination.FlushAsync(cancellationToken);
                    }

                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourceFullPath));
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // Best effort cleanup after a failed or cancelled copy.
                    }
                }

                copied.Add(destinationPath);
                progress?.Report($"[Preserve] Copied unchanged: {sourceFullPath} -> {destinationPath}");
            }
        }

        return copied;
    }
}
