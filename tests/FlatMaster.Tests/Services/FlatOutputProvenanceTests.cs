using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class FlatOutputProvenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
    private readonly DirectoryJob _job;
    private ExposureGroup Group => _job.ExposureGroups.Single();

    public FlatOutputProvenanceTests()
    {
        var source = Path.Combine(_root, "source", "Ha");
        Directory.CreateDirectory(source);
        var paths = Enumerable.Range(1, 3).Select(i => Path.Combine(source, $"flat_{i}.fits")).ToList();
        foreach (var path in paths) File.WriteAllText(path, "source frame");
        _job = new DirectoryJob
        {
            DirectoryPath = source, BaseRootPath = Path.GetDirectoryName(source)!,
            OutputRootPath = Path.Combine(_root, "output"), RelativeDirectory = "Ha",
            ExposureGroups =
            [
                new ExposureGroup
                {
                    ExposureTime = 5, FilePaths = paths,
                    RepresentativeMetadata = new ImageMetadata { FilePath = paths[0], Type = ImageType.Flat, Filter = "Ha" },
                    MatchingCriteria = new MatchingCriteria { Binning = "1", Gain = 100, Offset = 10, Width = 20, Height = 10, Channels = 1 }
                }
            ]
        };
    }

    [Theory]
    [InlineData("Native")]
    [InlineData("PixInsight")]
    public void CompletedManifest_RecordsInputsAndCameraSettingsAndDetectsCompletedOutput(string engine)
    {
        var manifest = Complete(engine);
        var saved = FlatOutputProvenance.Read(manifest.MasterPath + ".meta.json")!;
        saved.InputFrameCount.Should().Be(3);
        saved.Gain.Should().Be(100);
        saved.Offset.Should().Be(10);
        saved.Width.Should().Be(20);
        saved.Height.Should().Be(10);
        saved.SourceFrames.Should().OnlyContain(source => source.Length > 0 && Path.IsPathFullyQualified(source.Path));

        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.Completed);
        Group.NeedsProcessing.Should().BeFalse();
        Group.ExistingMasterPath.Should().Be(manifest.MasterPath);
    }

    [Fact]
    public void NewFramesDuringProcessing_AreDetectedAsChangedOnNextScan()
    {
        var manifest = Complete();
        var added = Path.Combine(_job.DirectoryPath, "flat_added.fits");
        File.WriteAllText(added, "new frame");
        Group.FilePaths.Add(added);

        Refresh();

        Group.OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
        Group.NeedsProcessing.Should().BeTrue();
        Group.ExistingMasterPath.Should().Be(manifest.MasterPath);
        Group.OutputStatusDetail.Should().Contain("3 input frames").And.Contain("4");
    }

    [Fact]
    public void ModifiedInputSameSize_IsDetectedByModificationTime()
    {
        Complete();
        File.SetLastWriteTimeUtc(Group.FilePaths[0], DateTime.UtcNow.AddMinutes(1));
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
    }

    [Fact]
    public void RemovedInput_IsDetectedAsChanged()
    {
        Complete();
        File.Delete(Group.FilePaths[0]);
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
    }

    [Fact]
    public void RemovedInputFromInventory_StillShowsChangedWhenBelowIntegrationMinimum()
    {
        var manifest = Complete();
        Group.FilePaths.RemoveAt(0);
        Refresh();
        Group.IsValid.Should().BeFalse();
        Group.OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
        Group.ExistingMasterPath.Should().Be(manifest.MasterPath);
    }

    [Theory]
    [InlineData(101, 10, 20, 10)]
    [InlineData(100, 11, 20, 10)]
    [InlineData(100, 10, 21, 10)]
    [InlineData(100, 10, 20, 11)]
    public void DifferentCameraSettings_DoNotReuseFilenameOnlyMatch(double gain, double offset, int width, int height)
    {
        Complete();
        var changed = new ExposureGroup
        {
            ExposureTime = 5, FilePaths = Group.FilePaths, RepresentativeMetadata = Group.RepresentativeMetadata,
            MatchingCriteria = Group.MatchingCriteria! with { Gain = gain, Offset = offset, Width = width, Height = height }
        };
        _job.ExposureGroups.Clear();
        _job.ExposureGroups.Add(changed);
        Refresh();
        changed.OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
        changed.NeedsProcessing.Should().BeTrue();
    }

    [Fact]
    public void FailedReprocess_PreservesPreviousMasterAndExposesFailureReason()
    {
        var completed = Complete();
        Refresh();
        var attempt = FlatOutputProvenance.Begin(_job, Group, "Native");
        FlatOutputProvenance.Fail(attempt, "Calibration failed");

        Refresh();

        Group.OutputStatus.Should().Be(FlatOutputStatus.Failed);
        Group.OutputStatusDetail.Should().Be("Calibration failed");
        Group.ExistingMasterPath.Should().Be(completed.MasterPath);
        FlatOutputProvenance.Read(completed.MasterPath + ".meta.json")!.State.Should().Be("Completed");
    }

    [Fact]
    public void InterruptedAttempt_IsFailedAndCanBeRetried()
    {
        FlatOutputProvenance.Begin(_job, Group, "PixInsight");
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.Failed);
        Group.OutputStatusDetail.Should().Contain("interrupted");
        Group.NeedsProcessing.Should().BeTrue();
    }

    [Fact]
    public void TruncatedOutput_IsNotConsideredCompleted()
    {
        var completed = Complete();
        File.WriteAllText(completed.MasterPath!, "x");
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.Failed);
    }

    [Fact]
    public void MissingOutput_BecomesPending()
    {
        var completed = Complete();
        File.Delete(completed.MasterPath!);
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.Pending);
        Group.ExistingMasterPath.Should().BeNull();
    }

    [Fact]
    public void MalformedState_IsVisibleAsFailure()
    {
        Complete();
        File.WriteAllText(FlatOutputProvenance.StatePath(_job, Group, _job.OutputRootPath), "invalid json");
        Refresh();
        Group.OutputStatus.Should().Be(FlatOutputStatus.Failed);
        Group.OutputStatusDetail.Should().Contain("Cannot verify");
    }

    [Fact]
    public void PixInsightReceipt_RecordsPublishedMasterEvenWhenAnotherGroupFails()
    {
        var attempt = FlatOutputProvenance.Begin(_job, Group, "PixInsight");
        var master = Path.Combine(attempt.OutputDirectory, "MasterFlat_UNKNOWNDATE_Ha_5s.xisf");
        File.WriteAllText(master, "output");
        var receipt = Path.Combine(_root, "receipt.json");
        File.WriteAllText(receipt, JsonSerializer.Serialize(new { masterPath = master, integratedFrameCount = 3 }));

        FlatOutputProvenance.CompleteFromReceipt(attempt, receipt);
        Refresh();

        Group.OutputStatus.Should().Be(FlatOutputStatus.Completed);
        FlatOutputProvenance.Read(master + ".meta.json")!.IntegratedFrameCount.Should().Be(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void PixInsightReceipt_RejectsInvalidFrameCounts(int count)
    {
        var attempt = FlatOutputProvenance.Begin(_job, Group, "PixInsight");
        var receipt = Path.Combine(_root, "receipt.json");
        File.WriteAllText(receipt, JsonSerializer.Serialize(new { masterPath = "invalid", integratedFrameCount = count }));
        Action complete = () => FlatOutputProvenance.CompleteFromReceipt(attempt, receipt);
        complete.Should().Throw<InvalidDataException>();
    }

    private FlatMasterManifest Complete(string engine = "Native")
    {
        var manifest = FlatOutputProvenance.Begin(_job, Group, engine);
        var path = Path.Combine(manifest.OutputDirectory, "MasterFlat_2026-10-11_Ha_Bin1_5.000s.xisf");
        File.WriteAllText(path, "completed master");
        FlatOutputProvenance.Complete(manifest, path);
        return manifest;
    }

    private void Refresh() => ExistingFlatOutputService.Refresh([_job], _job.OutputRootPath);

    public void Dispose() => Directory.Delete(_root, true);
}
