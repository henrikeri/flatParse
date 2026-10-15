using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class CombinedFlatProcessingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CombinedFlatsTests", Guid.NewGuid().ToString("N"));
    private static MatchingCriteria Criteria => new() { Binning = "1", Gain = 100, Offset = 10, Temperature = -10, Width = 8, Height = 6, Channels = 1 };
    private ExposureGroup Group(double exposure, params string[] names) => new()
    {
        ExposureTime = exposure, FilePaths = names.Select(n => Path.Combine(_root, n)).ToList(), MatchingCriteria = Criteria,
        RepresentativeMetadata = new ImageMetadata { FilePath = Path.Combine(_root, names[0]), Type = ImageType.Flat, ExposureTime = exposure, Filter = "Ha", Width = 8, Height = 6, Channels = 1 }
    };
    private DirectoryJob Job(params ExposureGroup[] groups) => new()
    {
        DirectoryPath = _root, BaseRootPath = _root, OutputRootPath = _root, RelativeDirectory = ".",
        ExposureGroups = groups.ToList(), PassthroughFiles = groups.Where(g => !g.IsValid).SelectMany(g => g.FilePaths).ToList()
    };

    [Fact]
    public void CombinesUnderfilledCompatibleGroupsAndRemovesPreservationCopies()
    {
        var job = CombinedFlatGrouping.Combine(Job(Group(20, "a.fit"), Group(30, "b.fit", "c.fit")));
        var group = job.ExposureGroups.Should().ContainSingle().Subject;
        group.Count.Should().Be(3); group.IsValid.Should().BeTrue();
        group.CalibrationGroups.Select(g => g.Count).Should().Equal(1, 2);
        group.OutputExposureTag.Should().Be("Mixed20-30");
        job.PassthroughFiles.Should().BeEmpty();
        CombinedFlatGrouping.Combine(job).ExposureGroups.Single().CalibrationGroups.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("gain")][InlineData("offset")][InlineData("binning")][InlineData("filter")]
    [InlineData("width")][InlineData("height")][InlineData("channels")]
    public void DoesNotMixDifferentCameraSettingsOrFilters(string field)
    {
        var a = Group(20, "a.fit", "b.fit");
        var original = Group(30, "c.fit");
        var b = new ExposureGroup
        {
            ExposureTime = original.ExposureTime, FilePaths = original.FilePaths,
            RepresentativeMetadata = original.RepresentativeMetadata! with { Filter = field == "filter" ? "OIII" : "Ha" },
            MatchingCriteria = Criteria with
            {
                Gain = field == "gain" ? 200 : 100, Offset = field == "offset" ? 20 : 10,
                Binning = field == "binning" ? "2" : "1", Width = field == "width" ? 16 : 8,
                Height = field == "height" ? 12 : 6, Channels = field == "channels" ? 3 : 1
            }
        };
        var job = CombinedFlatGrouping.Combine(Job(a, b));
        job.ExposureGroups.Should().HaveCount(2).And.OnlyContain(g => !g.IsCombined);
        job.PassthroughFiles.Should().HaveCount(3);
    }

    [Fact]
    public void TwoTotalFramesRemainPreservationCandidates()
    {
        var job = CombinedFlatGrouping.Combine(Job(Group(20, "a.fit"), Group(30, "b.fit")));
        job.ExposureGroups.Should().OnlyContain(g => !g.IsCombined);
        job.PassthroughFiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task Native_CalibratesEachExposureWithItsOwnDarkThenNormalizesTogether()
    {
        Directory.CreateDirectory(_root);
        var job = await WriteFixture();
        var darks = Darks();
        var matcher = new DarkMatchingService(NullLogger<DarkMatchingService>.Instance);
        var diagnostics = await matcher.GenerateMatchingDiagnosticsAsync([job], darks, new DarkMatchingOptions());
        diagnostics.Where(d => d.ExposureTime == 20).Should().OnlyContain(d => d.SelectedDark.FilePath == darks[0].FilePath);
        diagnostics.Where(d => d.ExposureTime == 30).Should().OnlyContain(d => d.SelectedDark.FilePath == darks[1].FilePath);
        var plan = Plan(job, darks);
        var engine = new NativeProcessingService(NullLogger<NativeProcessingService>.Instance, matcher);
        var result = await engine.ExecuteAsync(plan, new SilentProgress());
        result.Success.Should().BeTrue(); result.SucceededFiles.Should().Be(3);
        var path = Directory.GetFiles(_root, "MasterFlat_*Mixed20-30s.fits").Should().ContainSingle().Subject;
        var image = await new FitsImageIO(NullLogger.Instance).ReadAsync(path);
        for (var i = 0; i < image.Pixels.Length; i++) image.Pixels[i].Should().BeApproximately(0.4 * Shape(i), 0.000001);
        var manifest = FlatOutputProvenance.Read(path + ".meta.json")!;
        manifest.CalibrationExposures.Should().Equal(20, 30);
        manifest.IntegratedFrameCount.Should().Be(3);
        ExistingFlatOutputService.Refresh([job], _root);
        job.ExposureGroups.Single().OutputStatus.Should().Be(FlatOutputStatus.Completed);
        FlatOutputProvenance.CalibrationIdentity(job.ExposureGroups.Single()).Should().NotBe(FlatOutputProvenance.CalibrationIdentity(job.ExposureGroups.Single().CalibrationGroups[0]));
        File.AppendAllText(job.ExposureGroups.Single().FilePaths[0], "changed");
        ExistingFlatOutputService.Refresh([job], _root);
        job.ExposureGroups.Single().OutputStatus.Should().Be(FlatOutputStatus.ChangedInputs);
    }

    [Fact]
    public async Task MissingRequiredDarkForOneExposureFailsWholeCombinedMaster()
    {
        Directory.CreateDirectory(_root);
        var job = await WriteFixture();
        var plan = Plan(job, [Darks()[0]]);
        var engine = new NativeProcessingService(NullLogger<NativeProcessingService>.Instance, new DarkMatchingService(NullLogger<DarkMatchingService>.Instance));
        var result = await engine.ExecuteAsync(plan, new SilentProgress());
        result.Success.Should().BeFalse();
        Directory.GetFiles(_root, "MasterFlat_*.fits").Should().BeEmpty();
        ExistingFlatOutputService.Refresh([job], _root);
        job.ExposureGroups.Single().OutputStatus.Should().Be(FlatOutputStatus.Failed);
    }

    [Fact]
    public void PixInsightConfigCarriesOriginalFilesAndExposureSpecificCalibration()
    {
        var job = CombinedFlatGrouping.Combine(Job(Group(20, "a.fit"), Group(30, "b.fit", "c.fit")));
        var script = new PixInsightService(NullLogger<PixInsightService>.Instance).GeneratePJSRScript(Plan(job, []));
        script.Should().Contain("outputTag:\"Mixed20-30\"").And.Contain("calibrationGroups:[{exposure:20").And.Contain("},{exposure:30");
        script.Should().Contain("pickDarkFor(unit.exposure, unit.want").And.Contain("combinedFiles = combinedFiles.concat(unitFiles)")
            .And.Contain("integratedFrameCount:combinedFiles.length");
    }

    private static double Shape(int i) => 1 + (i % 8 - 3.5) * 0.04;
    private static double Dark(int i, double exposure) => exposure == 20 ? 0.01 + (i % 8) * 0.003 : 0.02 + (i % 8) * 0.007;
    private async Task<DirectoryJob> WriteFixture()
    {
        var groups = new[] { Group(20, "a.fit"), Group(30, "b.fit", "c.fit") };
        foreach (var g in groups)
        {
            foreach (var path in g.FilePaths)
                await WriteImage(path, g.ExposureTime, i => (g.ExposureTime == 20 ? 0.4 : 0.6) * Shape(i) + Dark(i, g.ExposureTime), "Flat Frame");
            await WriteImage(Path.Combine(_root, $"dark{g.ExposureTime}.fits"), g.ExposureTime, i => Dark(i, g.ExposureTime), "Master Dark");
        }
        return CombinedFlatGrouping.Combine(Job(groups));
    }
    private static Task WriteImage(string path, double exposure, Func<int, double> pixels, string type) => FitsImageIO.WriteFitsAsync(path, new FitsImageIO.ImageData
    {
        Width = 8, Height = 6, Channels = 1, Pixels = Enumerable.Range(0, 48).Select(pixels).ToArray(),
        Headers = new Dictionary<string, string> { ["IMAGETYP"] = type, ["EXPTIME"] = exposure.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    });
    private List<DarkFrame> Darks() => new[] { 20.0, 30.0 }.Select(exp => new DarkFrame
    {
        FilePath = Path.Combine(_root, $"dark{exp}.fits"), ExposureTime = exp, Type = ImageType.MasterDark,
        Binning = "1", Gain = 100, Offset = 10, Temperature = -10, Width = 8, Height = 6, Channels = 1
    }).ToList();
    private static ProcessingPlan Plan(DirectoryJob job, List<DarkFrame> darks) => new()
    {
        Jobs = [job], DarkCatalog = darks, Configuration = new ProcessingConfiguration
        {
            PixInsightExecutable = "PixInsight.exe", OutputFileExtension = "fits", RequireDarks = true,
            DarkMatching = new DarkMatchingOptions { AllowNearestExposureWithOptimize = false }
        }
    };
    private sealed class SilentProgress : IProgress<string> { public void Report(string value) { } }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
