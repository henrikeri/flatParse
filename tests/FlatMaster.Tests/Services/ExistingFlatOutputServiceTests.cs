using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class ExistingFlatOutputServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("MasterFlat_2026-10-10_Ha_Bin1_5.000s.xisf")]
    [InlineData("MasterFlat_2026-10-10_Ha_5s.fits")]
    [InlineData("MasterFlat_UNKNOWNDATE_Ha_5.000s.fit")]
    public void Refresh_FindsBothEngineFormatsAcrossDatesAndExtensions(string filename)
    {
        var job = CreateJob([CreateGroup(5), CreateGroup(6)]);
        WriteOutput(filename);

        ExistingFlatOutputService.Refresh([job], OutputRoot);

        job.ExposureGroups[0].ExistingMasterPath.Should().EndWith(filename);
        job.ExposureGroups[1].ExistingMasterPath.Should().BeNull();
        ExistingFlatOutputService.HasPendingWork(job, OutputRoot).Should().BeTrue();
    }

    [Theory]
    [InlineData("MasterFlat_2026-10-10_R_Bin1_5.000s.xisf", "session")]
    [InlineData("MasterFlat_2026-10-10_Ha_Bin2_5.000s.xisf", "session")]
    [InlineData("MasterFlat_2026-10-10_Ha_Bin1_5.000s.xisf", "unrelated")]
    [InlineData("MasterFlat_2026-10-10_Ha_Bin1_5.000s.xisf.tmp", "session")]
    public void Refresh_DoesNotConfuseOtherGroupsOrDirectories(string filename, string relative)
    {
        var job = CreateJob([CreateGroup(5)]);
        WriteOutput(filename, relative);

        ExistingFlatOutputService.Refresh([job], OutputRoot);

        job.ExposureGroups.Single().ExistingMasterPath.Should().BeNull();
    }

    [Fact]
    public void Refresh_LeavesOnly100NewGroupsPendingOutOf2500()
    {
        var job = CreateJob(Enumerable.Range(1, 2500).Select(i => CreateGroup(i)).ToList());
        for (var i = 1; i <= 2400; i++)
            WriteOutput($"MasterFlat_2026-10-10_Ha_Bin1_{i}s.xisf");

        ExistingFlatOutputService.Refresh([job], OutputRoot);

        job.ExposureGroups.Count(g => g.ExistingMasterPath == null).Should().Be(100);
        job.ExposureGroups.Take(2400).Should().OnlyContain(g => g.ExistingMasterPath != null);
        job.ExposureGroups.Skip(2400).Should().OnlyContain(g => g.ExistingMasterPath == null);
    }

    [Fact]
    public void Refresh_RechecksNewlyWrittenAndRemovedMasters()
    {
        var job = CreateJob([CreateGroup(5)]);
        ExistingFlatOutputService.Refresh([job], OutputRoot);
        job.ExposureGroups.Single().ExistingMasterPath.Should().BeNull();

        var output = WriteOutput("MasterFlat_2026-10-10_Ha_Bin1_5s.xisf");
        ExistingFlatOutputService.Refresh([job], OutputRoot);
        ExistingFlatOutputService.HasPendingWork(job, OutputRoot).Should().BeFalse();

        File.Delete(output);
        ExistingFlatOutputService.Refresh([job], OutputRoot);
        job.ExposureGroups.Single().ExistingMasterPath.Should().BeNull();
    }

    [Fact]
    public void Refresh_RecognizesAlreadyCopiedSingleMasters()
    {
        var sourceDirectory = Path.Combine(_root, "input", "session");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "existing_master.xisf");
        File.WriteAllText(source, "master");
        var job = CreateJob([]);
        job.PassthroughFiles.Add(source);
        ExistingFlatOutputService.HasPendingWork(job, OutputRoot).Should().BeTrue();

        var destination = WriteOutput("existing_master.xisf");
        ExistingFlatOutputService.IsPreserved(job, OutputRoot, source).Should().BeTrue();
        ExistingFlatOutputService.HasPendingWork(job, OutputRoot).Should().BeFalse();

        File.WriteAllText(destination, "partial");
        ExistingFlatOutputService.IsPreserved(job, OutputRoot, source).Should().BeFalse();
    }

    private string OutputRoot => Path.Combine(_root, "custom-output");

    private DirectoryJob CreateJob(List<ExposureGroup> groups) => new()
    {
        DirectoryPath = Path.Combine(_root, "input", "session"),
        BaseRootPath = Path.Combine(_root, "input"),
        OutputRootPath = Path.Combine(_root, "wrong-default-output"),
        RelativeDirectory = "session",
        ExposureGroups = groups
    };

    private static ExposureGroup CreateGroup(double exposure) => new()
    {
        ExposureTime = exposure,
        FilePaths = ["flat_1.fit", "flat_2.fit", "flat_3.fit"],
        RepresentativeMetadata = new ImageMetadata { FilePath = "flat_1.fit", Type = ImageType.Flat, Filter = "Ha" },
        MatchingCriteria = new MatchingCriteria { Binning = "1" }
    };

    private string WriteOutput(string filename, string relative = "session")
    {
        var directory = Path.Combine(OutputRoot, relative);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, filename);
        File.WriteAllText(path, "master");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
