using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using Microsoft.Extensions.Logging;

namespace FlatMaster.Infrastructure.Services;

/// <summary>
/// Manages output path configuration and directory structure replication
/// </summary>
public sealed class OutputPathService : IOutputPathService
{
    private readonly ILogger<OutputPathService> _logger;

    public OutputPathService(ILogger<OutputPathService> logger)
    {
        _logger = logger;
    }

    public string GetOutputPath(
        string sourceFilePath,
        string sourceRoot,
        OutputPathConfiguration config,
        string fileType)
    {
        var fileName = Path.GetFileName(sourceFilePath);

        return config.Mode switch
        {
            OutputMode.InlineInSource => GetInlineOutputPath(sourceFilePath, fileType, config),
            OutputMode.ReplicatedSeparateTree => GetReplicatedOutputPath(sourceFilePath, sourceRoot, fileType, config),
            _ => throw new ArgumentException($"Unknown output mode: {config.Mode}")
        };
    }

    public async Task InitializeOutputDirectoriesAsync(OutputPathConfiguration config)
    {
        var dirsToCreate = new[]
        {
            config.OutputRootPath,
            Path.Combine(config.OutputRootPath, config.DarkMastersSubdir),
            Path.Combine(config.OutputRootPath, config.CalibratedFlatsSubdir),
            Path.Combine(config.OutputRootPath, config.MasterCalibrationSubdir)
        };

        foreach (var dir in dirsToCreate)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                _logger.LogInformation("Created output directory: {Directory}", dir);
            }
        }

        await Task.CompletedTask;
    }

    public async Task ReplicateDirectoryStructureAsync(
        string sourceRoot,
        OutputPathConfiguration config)
    {
        if (config.Mode != OutputMode.ReplicatedSeparateTree)
            return;

        _logger.LogInformation("Replicating directory structure from {Source} to {Dest}", 
            sourceRoot, config.OutputRootPath);

        try
        {
            ReplicateDirectoriesRecursive(sourceRoot, sourceRoot, config);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to replicate directory structure");
            throw;
        }
    }

    private string GetInlineOutputPath(
        string sourceFilePath,
        string fileType,
        OutputPathConfiguration config)
    {
        var directory = Path.GetDirectoryName(sourceFilePath)!;
        var fileName = Path.GetFileName(sourceFilePath);

        return fileType switch
        {
            "dark_master" => Path.Combine(directory, config.DarkMastersSubdir, fileName),
            "calibrated_flat" => Path.Combine(directory, config.CalibratedFlatsSubdir, fileName),
            "master_calibration" => Path.Combine(directory, config.MasterCalibrationSubdir, fileName),
            _ => Path.Combine(directory, fileName)
        };
    }

    private string GetReplicatedOutputPath(
        string sourceFilePath,
        string sourceRoot,
        string fileType,
        OutputPathConfiguration config)
    {
        var fileName = Path.GetFileName(sourceFilePath);
        var relativePath = Path.GetRelativePath(sourceRoot, sourceFilePath);
        var relativeDir = Path.GetDirectoryName(relativePath)!;

        var outputDir = fileType switch
        {
            "dark_master" => Path.Combine(config.OutputRootPath, config.DarkMastersSubdir),
            "calibrated_flat" => Path.Combine(config.OutputRootPath, config.CalibratedFlatsSubdir),
            "master_calibration" => Path.Combine(config.OutputRootPath, config.MasterCalibrationSubdir),
            _ => Path.Combine(config.OutputRootPath, relativeDir)
        };

        // If it's calibrated flats or replicated structure, preserve relative path
        if (fileType == "calibrated_flat" && config.ReplicateDirectoryStructure)
        {
            var calibDir = Path.Combine(config.OutputRootPath, config.CalibratedFlatsSubdir, relativeDir);
            return Path.Combine(calibDir, fileName);
        }

        if (fileType == "master_calibration" && config.ReplicateDirectoryStructure)
        {
            var masterDir = Path.Combine(config.OutputRootPath, config.MasterCalibrationSubdir, relativeDir);
            return Path.Combine(masterDir, fileName);
        }

        return Path.Combine(outputDir, fileName);
    }

    private void ReplicateDirectoriesRecursive(
        string sourceDir,
        string sourceRoot,
        OutputPathConfiguration config)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, sourceDir);
        var targetDir = relativePath == "." 
            ? config.OutputRootPath 
            : Path.Combine(config.OutputRootPath, relativePath);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            _logger.LogDebug("Created directory: {Directory}", targetDir);
        }

        // Recursively replicate subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            ReplicateDirectoriesRecursive(subDir, sourceRoot, config);
        }
    }
}
