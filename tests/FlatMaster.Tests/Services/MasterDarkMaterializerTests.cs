using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlatMaster.Core.Interfaces;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace FlatMaster.Tests.Services;

public class MasterDarkMaterializerTests
{
    [Theory]
    [InlineData(ImageType.MasterDark)]
    [InlineData(ImageType.MasterDarkFlat)]
    public async Task MaterializeMastersAsync_ImportsOnlyMatchedMastersOnce(ImageType masterType)
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var needed = CreateImportableMaster(root, "needed", 5, masterType);
            var unused = CreateImportableMaster(root, "unused", 30, masterType);
            var outputRoot = Path.Combine(root, "output");
            var plan = CreateImportPlan(root, outputRoot, [needed, unused],
                [CreateFlatGroup(5), CreateFlatGroup(5)]);

            var imported = await CreateMaterializer().MaterializeMastersAsync(plan, [], true, null);

            imported.Should().ContainSingle();
            File.ReadAllText(imported.Single()).Should().Be("needed");
            Directory.GetFiles(outputRoot, "*.xisf", SearchOption.AllDirectories)
                .Should().Equal(imported);
            plan.DarkCatalog.Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, 3, 5)]
    [InlineData(true, 2, 5)]
    [InlineData(true, 3, 100)]
    public async Task MaterializeMastersAsync_DoesNotImportWithoutAProcessingMatch(
        bool jobSelected, int flatCount, double flatExposure)
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var master = CreateImportableMaster(root, "unused", 5, ImageType.MasterDark);
            var outputRoot = Path.Combine(root, "output");
            var plan = CreateImportPlan(root, outputRoot, [master],
                [CreateFlatGroup(flatExposure, flatCount)]);
            plan.Jobs.Single().IsSelected = jobSelected;

            var imported = await CreateMaterializer().MaterializeMastersAsync(plan, [], true, null);

            imported.Should().BeEmpty();
            Directory.Exists(outputRoot).Should().BeFalse();
            plan.DarkCatalog.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MaterializeMastersAsync_ImportsTheManualMatchInsteadOfTheAutomaticCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var automatic = CreateImportableMaster(root, "automatic", 5, ImageType.MasterDark);
            var manual = CreateImportableMaster(root, "manual", 30, ImageType.MasterDarkFlat);
            var plan = CreateImportPlan(root, Path.Combine(root, "output"), [automatic, manual],
                [CreateFlatGroup(5, manualPath: manual.FilePath)]);

            var imported = await CreateMaterializer().MaterializeMastersAsync(plan, [], true, null);

            imported.Should().ContainSingle();
            File.ReadAllText(imported.Single()).Should().Be("manual");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MaterializeMastersAsync_ImportsMatchedMastersWhenRawDarksAlsoNeedIntegration()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var needed = CreateImportableMaster(root, "needed", 5, ImageType.MasterDark);
            var unused = CreateImportableMaster(root, "unused", 90, ImageType.MasterDark);
            var rawFolder = Path.Combine(root, "DARKS_30s");
            Directory.CreateDirectory(rawFolder);
            var metadata = new Dictionary<string, ImageMetadata>();
            var catalog = new List<DarkFrame> { needed, unused };
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(rawFolder, $"dark_{i}.xisf");
                await FitsImageIO.WriteXisfAsync(path, new FitsImageIO.ImageData
                {
                    Width = 2, Height = 2, Channels = 1,
                    Pixels = [0.1, 0.1, 0.1, 0.1],
                    Headers = new Dictionary<string, string>()
                });
                metadata[path] = new ImageMetadata
                {
                    FilePath = path, Type = ImageType.Dark, ExposureTime = 30, Binning = "1"
                };
                catalog.Add(new DarkFrame
                {
                    FilePath = path, Type = ImageType.Dark, ExposureTime = 30,
                    Binning = "1", DarkGroupFolder = rawFolder
                });
            }
            var reader = new Mock<IMetadataReaderService>();
            reader.Setup(r => r.ReadMetadataBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(metadata);
            var materializer = new MasterDarkMaterializer(
                new Mock<ILogger<MasterDarkMaterializer>>().Object, reader.Object,
                new DarkMatchingService(new Mock<ILogger<DarkMatchingService>>().Object),
                new Mock<IPixInsightService>().Object, new Mock<IImageProcessingEngine>().Object);
            var plan = CreateImportPlan(root, Path.Combine(root, "output"), catalog,
                [CreateFlatGroup(5), CreateFlatGroup(30)]);

            var masters = await materializer.MaterializeMastersAsync(plan, [root], true, null);

            masters.Should().HaveCount(2);
            masters.Should().Contain(path => File.ReadAllText(path) == "needed");
            plan.DarkCatalog.Count(d => d.Type == ImageType.MasterDark && d.ExposureTime == 30)
                .Should().Be(1);
            Directory.GetFiles(Path.Combine(root, "output"), "*.xisf", SearchOption.AllDirectories)
                .Should().BeEquivalentTo(masters);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MaterializeMastersAsync_ImportsBothDarksForUnderfilledCombinedExposures()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dark20 = CreateImportableMaster(root, "twenty", 20, ImageType.MasterDark);
            var dark30 = CreateImportableMaster(root, "thirty", 30, ImageType.MasterDark);
            var first = new ExposureGroup { ExposureTime = 20, FilePaths = ["a.fit"], MatchingCriteria = new MatchingCriteria { Binning = "1" } };
            var second = new ExposureGroup { ExposureTime = 30, FilePaths = ["b.fit", "c.fit"], MatchingCriteria = new MatchingCriteria { Binning = "1" } };
            var plan = CreateImportPlan(root, Path.Combine(root, "output"), [dark20, dark30], [first, second]);
            plan.Jobs[0] = CombinedFlatGrouping.Combine(plan.Jobs[0]);
            plan.Jobs[0].ExposureGroups.Single().IsCombined.Should().BeTrue();
            var imports = await CreateMaterializer().MaterializeMastersAsync(plan, [], true, null);
            imports.Should().HaveCount(2);
            imports.Select(File.ReadAllText).Should().BeEquivalentTo("twenty", "thirty");
        }
        finally { Directory.Delete(root, true); }
    }

    private static MasterDarkMaterializer CreateMaterializer()
        => new(
            new Mock<ILogger<MasterDarkMaterializer>>().Object,
            new Mock<IMetadataReaderService>().Object,
            new DarkMatchingService(new Mock<ILogger<DarkMatchingService>>().Object),
            new Mock<IPixInsightService>().Object,
            new Mock<IImageProcessingEngine>().Object);

    private static DarkFrame CreateImportableMaster(string root, string name, double exposure, ImageType type)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MasterDark.xisf");
        File.WriteAllText(path, name);
        File.WriteAllText(Path.Combine(directory, "MasterDark.meta.json"),
            JsonSerializer.Serialize(new MasterDarkManifest
            {
                Key = name,
                ExposureSeconds = exposure,
                SourceFrames = []
            }));
        return new DarkFrame { FilePath = path, ExposureTime = exposure, Type = type, Binning = "1" };
    }

    private static ExposureGroup CreateFlatGroup(double exposure, int count = 3, string? manualPath = null)
        => new()
        {
            ExposureTime = exposure,
            FilePaths = Enumerable.Range(0, count).Select(i => $"flat_{i}.fit").ToList(),
            MatchingCriteria = new MatchingCriteria { Binning = "1", ManualDarkPath = manualPath }
        };

    private static ProcessingPlan CreateImportPlan(
        string root, string outputRoot, List<DarkFrame> catalog, List<ExposureGroup> groups)
        => new()
        {
            Jobs =
            [
                new DirectoryJob
                {
                    DirectoryPath = root,
                    BaseRootPath = root,
                    OutputRootPath = outputRoot,
                    RelativeDirectory = ".",
                    ExposureGroups = groups
                }
            ],
            DarkCatalog = catalog,
            Configuration = new ProcessingConfiguration { PixInsightExecutable = "" }
        };

    [Theory]
    [InlineData(-10.9, -9.8, 1.0, true)]
    [InlineData(-11.0, -9.0, 1.0, true)]
    [InlineData(-11.1, -8.9, 1.0, false)]
    [InlineData(-12.0, -8.0, 5.0, true)]
    public void IsTemperatureSpreadAllowed_UsesToleranceAsPlusOrMinusRadius(
        double minimum,
        double maximum,
        double toleranceRadius,
        bool expected)
    {
        MasterDarkMaterializer.IsTemperatureSpreadAllowed([minimum, maximum], toleranceRadius)
            .Should().Be(expected);
    }

    [Fact]
    public void GetMaterializationTemperatureTolerance_HonorsConfiguredDarkDeltaLimit()
    {
        var options = new DarkMatchingOptions
        {
            MaxTempDeltaC = 1.0,
            DarkOverBiasTempDeltaC = 5.0
        };

        MasterDarkMaterializer.GetMaterializationTemperatureTolerance(options).Should().Be(5.0);
    }

    [Fact]
    public async Task PreviewDarksOnlyMaterializationAsync_GroupsByExposureAndTemperatureTolerance()
    {
        var logger = new Mock<ILogger<MasterDarkMaterializer>>().Object;
        var metadataReader = new Mock<IMetadataReaderService>().Object;
        var matcher = new Mock<IDarkMatchingService>().Object;
        var pixInsight = new Mock<IPixInsightService>().Object;
        var native = new Mock<IImageProcessingEngine>().Object;
        var materializer = new MasterDarkMaterializer(logger, metadataReader, matcher, pixInsight, native);

        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a.fit");
            var b = Path.Combine(root, "b.fit");
            var c = Path.Combine(root, "c.fit");
            var d = Path.Combine(root, "d.fit");
            var master = Path.Combine(root, "master.xisf");
            File.WriteAllText(a, string.Empty);
            File.WriteAllText(b, string.Empty);
            File.WriteAllText(c, string.Empty);
            File.WriteAllText(d, string.Empty);
            File.WriteAllText(master, string.Empty);

            var plan = new ProcessingPlan
            {
                Jobs =
                [
                    new DirectoryJob
                    {
                        DirectoryPath = root,
                        BaseRootPath = root,
                        OutputRootPath = root,
                        RelativeDirectory = "__DARKS_ONLY__",
                        ExposureGroups = [],
                        IsSelected = true
                    }
                ],
                DarkCatalog =
                [
                    new DarkFrame { FilePath = a, Type = ImageType.Dark, ExposureTime = 25.0, Temperature = -10.2, Binning = "1", Gain = 100, IsSelected = true },
                    new DarkFrame { FilePath = b, Type = ImageType.Dark, ExposureTime = 25.0, Temperature = -9.7, Binning = "1", Gain = 100, IsSelected = true },
                    new DarkFrame { FilePath = c, Type = ImageType.Dark, ExposureTime = 25.0, Temperature = -9.4, Binning = "1", Gain = 100, IsSelected = true },
                    new DarkFrame { FilePath = d, Type = ImageType.Dark, ExposureTime = 25.0, Temperature = -7.0, Binning = "1", Gain = 100, IsSelected = true },
                    new DarkFrame { FilePath = master, Type = ImageType.MasterDark, ExposureTime = 25.0, Temperature = -10.0, Binning = "1", Gain = 100, IsSelected = true }
                ],
                Configuration = new ProcessingConfiguration
                {
                    PixInsightExecutable = "C:/Program Files/PixInsight/bin/PixInsight.exe",
                    DarkMatching = new DarkMatchingOptions(),
                    Rejection = new RejectionSettings()
                }
            };

            var preview = await materializer.PreviewDarksOnlyMaterializationAsync(plan, 1.0);

            preview.Should().HaveCount(2);
            preview.Select(x => x.FrameCount).OrderBy(x => x).Should().Equal(1, 3);
            preview.All(x => x.ExposureSeconds == 25.0).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CanReuseExistingMasterAsync_NewSourceFrameInvalidatesManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "FlatMasterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var master = Path.Combine(root, "master.xisf");
            var manifestPath = Path.Combine(root, "MasterDark.meta.json");
            var first = Path.Combine(root, "dark1.fit");
            var added = Path.Combine(root, "dark2.fit");
            File.WriteAllText(master, "master");
            File.WriteAllText(first, "one");
            File.WriteAllText(added, "two");

            var masterInfo = new FileInfo(master);
            var firstInfo = new FileInfo(first);
            var manifest = new MasterDarkManifest
            {
                PipelineVersion = 4,
                Key = "key",
                ExposureSeconds = 25,
                MasterFileLength = masterInfo.Length,
                MasterLastWriteUtc = masterInfo.LastWriteTimeUtc,
                SourceFrames =
                [
                    new SourceFrameInfo
                    {
                        Path = first,
                        LastWriteUtc = firstInfo.LastWriteTimeUtc,
                        Length = firstInfo.Length
                    }
                ]
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

            var reusableBeforeAddition = await MasterDarkMaterializer.CanReuseExistingMasterAsync(
                master,
                manifestPath,
                "key",
                new[] { first },
                CancellationToken.None);
            var reusableAfterAddition = await MasterDarkMaterializer.CanReuseExistingMasterAsync(
                master,
                manifestPath,
                "key",
                new[] { first, added },
                CancellationToken.None);

            reusableBeforeAddition.Should().BeTrue();
            reusableAfterAddition.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
