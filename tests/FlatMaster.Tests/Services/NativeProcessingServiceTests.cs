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

public sealed class NativeProcessingServiceTests
{
    [Fact]
    public async Task ExecuteAsync_MixedSignalStack_RejectsDarkFloorFramesAndWritesMaster()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = await WriteConstantFlatsAsync(root, [0.001, 0.30, 0.40, 0.50]);
            var (service, plan, progress) = BuildExecution(root, paths);

            var result = await service.ExecuteAsync(plan, progress);

            result.Success.Should().BeTrue();
            Directory.GetFiles(root, "MasterFlat_*.fits").Should().ContainSingle();
            progress.Messages.Should().Contain(message =>
                message.Contains("accepted 3/4, rejected 1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AllLowSignalStack_FailsWithoutWritingMaster()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = await WriteConstantFlatsAsync(root, [0.001, 0.002, 0.003, 0.004]);
            var (service, plan, progress) = BuildExecution(root, paths);

            var result = await service.ExecuteAsync(plan, progress);

            result.Success.Should().BeFalse();
            result.FailedBatches.Should().Be(1);
            Directory.GetFiles(root, "MasterFlat_*.fits").Should().BeEmpty();
            progress.Messages.Should().Contain(message =>
                message.Contains("no meaningful master flat can be generated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static (NativeProcessingService Service, ProcessingPlan Plan, RecordingProgress Progress) BuildExecution(
        string root,
        List<string> paths)
    {
        var matcher = new DarkMatchingService(NullLogger<DarkMatchingService>.Instance);
        var service = new NativeProcessingService(NullLogger<NativeProcessingService>.Instance, matcher);
        var plan = new ProcessingPlan
        {
            Jobs =
            [
                new DirectoryJob
                {
                    DirectoryPath = root,
                    BaseRootPath = root,
                    OutputRootPath = root,
                    RelativeDirectory = ".",
                    IsSelected = true,
                    ExposureGroups =
                    [
                        new ExposureGroup
                        {
                            ExposureTime = 20,
                            FilePaths = paths,
                            RepresentativeMetadata = new ImageMetadata
                            {
                                FilePath = paths[0],
                                Type = ImageType.Flat,
                                ExposureTime = 20,
                                Filter = "LUM",
                                Width = 8,
                                Height = 6,
                                Channels = 1
                            },
                            MatchingCriteria = new MatchingCriteria
                            {
                                Width = 8,
                                Height = 6,
                                Channels = 1
                            }
                        }
                    ]
                }
            ],
            DarkCatalog = [],
            Configuration = new ProcessingConfiguration
            {
                PixInsightExecutable = "PixInsight.exe",
                OutputFileExtension = "fits",
                RequireDarks = false,
                MinimumCalibratedFlatMedian = 0.01,
                MaxParallelism = 2
            }
        };
        return (service, plan, new RecordingProgress());
    }

    private static async Task<List<string>> WriteConstantFlatsAsync(string root, IReadOnlyList<double> medians)
    {
        var paths = new List<string>();
        for (var index = 0; index < medians.Count; index++)
        {
            var path = Path.Combine(root, $"flat_{index:000}.fits");
            await FitsImageIO.WriteFitsAsync(
                path,
                new FitsImageIO.ImageData
                {
                    Width = 8,
                    Height = 6,
                    Channels = 1,
                    Pixels = Enumerable.Repeat(medians[index], 8 * 6).ToArray()
                });
            paths.Add(path);
        }

        return paths;
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
}
