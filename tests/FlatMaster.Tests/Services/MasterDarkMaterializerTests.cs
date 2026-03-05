using System;
using System.IO;
using System.Linq;
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
}
