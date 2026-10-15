using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlatMaster.Core.Models;
using FlatMaster.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace FlatMaster.Tests.Services;

public sealed class FlatVerificationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FlatVerificationTests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output");
    public FlatVerificationServiceTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Output); }
    private string Write(string root, string path, string contents = "not an image header")
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, contents);
        return file;
    }
    private FlatVerificationResult Compare() => new FlatVerificationService().Compare(Source, Output);

    [Fact]
    public void FindsNestedMastersWithoutReadingImageContents()
    {
        var source = Write(Source, "session/Ha/raw.FITS");
        var master = Write(Output, "session/Ha/MasterFlat_2026-10-11_Ha_30s.XISF");
        using var lockedSource = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var lockedMaster = File.Open(master, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Compare();
        result.AllMastersPresent.Should().BeTrue();
        var row = result.Rows.Should().ContainSingle().Subject;
        row.SourceCount.Should().Be(1); row.MasterCount.Should().Be(1);
        row.RelativeDirectory.Should().Be(Path.Combine("session", "Ha"));
        row.Detail.Should().Contain("integrity were not checked");
    }

    [Fact]
    public void MissingRowsComeFirstAndUnrelatedMastersDoNotCount()
    {
        Write(Source, "a/raw.fit"); Write(Source, "b/raw.xisf"); Write(Source, "c/raw.fits");
        Write(Output, "a/MasterFlat_UNKNOWNDATE_Ha_5s.fit");
        Write(Output, "unrelated/MasterFlat_UNKNOWNDATE_Ha_5s.fit");
        Write(Output, "c/MasterDark.xisf"); Write(Output, "c/MasterFlat_UNKNOWNDATE_Ha_5s.xisf.tmp");
        var result = Compare();
        result.AllMastersPresent.Should().BeFalse();
        result.Rows.Select(row => row.Status).Should().Equal(FlatVerificationStatus.Missing, FlatVerificationStatus.Missing, FlatVerificationStatus.MasterFound);
        result.Rows.Single(row => row.RelativeDirectory == "b").Detail.Should().Contain("folder is missing");
        result.Rows.Single(row => row.RelativeDirectory == "c").Detail.Should().Contain("no master flat");
    }

    [Fact]
    public void CountsMultipleMasterFormatsAndSourceImagesAtRoot()
    {
        Write(Source, "raw.fit"); Write(Source, "raw2.fits"); Write(Source, "ignore.txt");
        Write(Output, "MasterFlat_2026-10-11_Ha_Bin1_30.000s.xisf");
        Write(Output, "Master Flat 2.fit"); Write(Output, "master_flat_3.fits");
        var row = Compare().Rows.Single();
        row.RelativeDirectory.Should().Be("."); row.SourceCount.Should().Be(2); row.MasterCount.Should().Be(3);
    }

    [Fact]
    public void EmptyMasterFailsEvenWhenAnotherMasterIsPresent()
    {
        Write(Source, "Ha/raw.fits");
        Write(Output, "Ha/MasterFlat_good.xisf"); Write(Output, "Ha/MasterFlat_empty.fit", "");
        var result = Compare();
        result.AllMastersPresent.Should().BeFalse();
        result.Rows.Single().Status.Should().Be(FlatVerificationStatus.Failed);
        result.Rows.Single().MasterCount.Should().Be(1);
    }

    [Fact]
    public void SkipsProcessingFoldersAndSourceMastersButIncludesOrdinaryHiddenFolders()
    {
        Write(Source, "_CalibratedFlats_30s/cal.fits"); Write(Source, ".FlatMaster/state.xisf");
        Write(Source, "_darkmasters/dark.fit"); Write(Source, "MasterFlat_existing.fit"); Write(Source, ".ordinary/raw.fit");
        Compare().Rows.Should().ContainSingle().Which.RelativeDirectory.Should().Be(".ordinary");
    }

    [Fact]
    public void EmptySourceIsNotReportedAsComplete()
    {
        var result = Compare();
        result.AllMastersPresent.Should().BeFalse(); result.Summary.Should().Contain("nothing checked");
    }

    [Fact]
    public void RejectsOverlappingAndMissingRoots()
    {
        var service = new FlatVerificationService();
        Action same = () => service.Compare(Source, Source);
        Action nested = () => service.Compare(_root, Source);
        Action absent = () => service.Compare(Source, Path.Combine(_root, "absent"));
        same.Should().Throw<ArgumentException>(); nested.Should().Throw<ArgumentException>(); absent.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void CancellationStopsDirectoryWalk()
    {
        Action compare = () => new FlatVerificationService().Compare(Source, Output, cancellationToken: new CancellationToken(true));
        compare.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void CsvContainsAuditScopePathsAndEscapesValues()
    {
        var result = new FlatVerificationResult(Source, Output, DateTime.UtcNow,
            [new("=folder,\"quoted\"", 20, 1, FlatVerificationStatus.MasterFound, "master.xisf", "Presence only")]);
        var csv = FlatVerificationService.ExportCsv(result);
        csv.Should().Contain("Folder-level output presence only").And.Contain(Source).And.Contain(Output)
            .And.Contain("\"'=folder,\"\"quoted\"\"\"").And.Contain("Master found");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void RecognizesPreservedCopiesDespiteFolderHavingThreeOrFourSources(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = $"FlatWizard/Ha/Exposure{(i < 2 ? 20 : 30)}_{i}.xisf";
            Write(Source, path); Write(Output, path);
        }
        var result = Compare();
        result.AllOutputsPresent.Should().BeTrue();
        result.AllMastersPresent.Should().BeFalse();
        result.Rows.Single().Status.Should().Be(FlatVerificationStatus.CopiesPresent);
        result.Rows.Single().MasterCount.Should().Be(0);
        result.Rows.Single().Detail.Should().Contain("File contents were not checked");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrDifferentSizeCopyDoesNotPass(bool wrongSize)
    {
        Write(Source, "Ha/raw1.fit"); Write(Source, "Ha/raw2.fit");
        Write(Output, "Ha/raw1.fit");
        if (wrongSize) Write(Output, "Ha/raw2.fit", "short");
        var result = Compare();
        result.AllOutputsPresent.Should().BeFalse();
        result.Rows.Single().Status.Should().Be(FlatVerificationStatus.Missing);
        result.Rows.Single().Detail.Should().Contain("1/2 source copies");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
