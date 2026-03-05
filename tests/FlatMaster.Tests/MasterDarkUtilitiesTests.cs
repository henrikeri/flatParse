using System;
using FlatMaster.Core.Utilities;
using Xunit;

namespace FlatMaster.Tests;

public class MasterDarkUtilitiesTests
{
    [Theory]
    [InlineData("DARKS_30", 30.0)]
    [InlineData("dark-2.5s", 2.5)]
    [InlineData("DARKS 120sec", 120.0)]
    [InlineData("no-number", null)]
    public void ExtractExposureFromFolderName_Works(string input, double? expected)
    {
        var got = MasterDarkUtilities.ExtractExposureFromFolderName(input);
        if (expected.HasValue)
        {
            Assert.NotNull(got);
            Assert.Equal(expected.Value, got!.Value, 3);
        }
        else
            Assert.Null(got);
    }

    [Fact]
    public void ComputeMasterKeyHash_IsDeterministic()
    {
        var a = MasterDarkUtilities.ComputeMasterKeyHash(30.0, "1", 0.0, 1024, 768);
        var b = MasterDarkUtilities.ComputeMasterKeyHash(30.0, "1", 0.0, 1024, 768);
        Assert.Equal(a, b);
        var c = MasterDarkUtilities.ComputeMasterKeyHash(30.001, "1", 0.0, 1024, 768);
        Assert.NotEqual(a, c);
    }
}
