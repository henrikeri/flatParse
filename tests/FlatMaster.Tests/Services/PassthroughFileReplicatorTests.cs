// Copyright (C) 2026 Henrik E. Riise
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class PassthroughFileReplicatorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "FlatMaster_PreserveTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyAsync_ReplicatesSingleImageUnchangedAndOverwritesStaleDestination()
    {
        var inputRoot = Path.Combine(_tempRoot, "input");
        var sourceDirectory = Path.Combine(inputRoot, "Rosette", "L");
        var outputRoot = Path.Combine(_tempRoot, "output");
        var destinationDirectory = Path.Combine(outputRoot, "Rosette", "L");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var source = Path.Combine(sourceDirectory, "MasterFlat_existing.xisf");
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        var expectedBytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(source, expectedBytes);
        await File.WriteAllTextAsync(destination, "stale");
        var sourceTime = new DateTime(2026, 8, 13, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, sourceTime);

        var job = BuildJob(sourceDirectory, inputRoot, outputRoot, source);

        var copied = await PassthroughFileReplicator.CopyAsync([job]);

        copied.Should().Equal(destination);
        File.Exists(source).Should().BeTrue();
        (await File.ReadAllBytesAsync(destination)).Should().BeEquivalentTo(expectedBytes);
        File.GetLastWriteTimeUtc(destination).Should().BeCloseTo(sourceTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CopyAsync_RejectsDestinationOutsideOutputRoot()
    {
        var inputRoot = Path.Combine(_tempRoot, "input");
        var sourceDirectory = Path.Combine(inputRoot, "single");
        var outputRoot = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "flat.fits");
        await File.WriteAllTextAsync(source, "flat");
        var job = BuildJob(sourceDirectory, inputRoot, outputRoot, source, Path.Combine("..", "escape"));

        var action = () => PassthroughFileReplicator.CopyAsync([job]);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside the output root*");
    }

    [Fact]
    public async Task CopyAsync_ReplicatesBothFilesFromSelectedTwoFrameGroup()
    {
        var inputRoot = Path.Combine(_tempRoot, "input");
        var sourceDirectory = Path.Combine(inputRoot, "two-frames");
        var outputRoot = Path.Combine(_tempRoot, "output");
        Directory.CreateDirectory(sourceDirectory);
        var first = Path.Combine(sourceDirectory, "flat_001.fit");
        var second = Path.Combine(sourceDirectory, "flat_002.fit");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var job = new DirectoryJob
        {
            DirectoryPath = sourceDirectory,
            BaseRootPath = inputRoot,
            OutputRootPath = outputRoot,
            RelativeDirectory = "two-frames",
            ExposureGroups = [],
            PassthroughFiles = [first, second]
        };

        var copied = await PassthroughFileReplicator.CopyAsync([job]);

        copied.Should().HaveCount(2);
        (await File.ReadAllTextAsync(Path.Combine(outputRoot, "two-frames", "flat_001.fit")))
            .Should().Be("first");
        (await File.ReadAllTextAsync(Path.Combine(outputRoot, "two-frames", "flat_002.fit")))
            .Should().Be("second");
    }

    private static DirectoryJob BuildJob(
        string sourceDirectory,
        string inputRoot,
        string outputRoot,
        string source,
        string relativeDirectory = "Rosette\\L")
        => new()
        {
            DirectoryPath = sourceDirectory,
            BaseRootPath = inputRoot,
            OutputRootPath = outputRoot,
            RelativeDirectory = relativeDirectory,
            ExposureGroups = [],
            PassthroughFiles = [source]
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup for temporary test files.
        }
    }
}
