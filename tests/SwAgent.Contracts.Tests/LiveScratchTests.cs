using System;
using System.IO;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class LiveScratchTests
{
    [Fact]
    public void The_scratch_root_is_under_local_app_data()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "live-scratch"),
            LiveScratch.Root);
    }

    [Theory]
    [InlineData(@"run-1\killed.SLDPRT", true)]
    [InlineData(@"run-1\killed.sldprt", true)]
    [InlineData(@"run-1\notes.txt", false)]
    [InlineData(@"..\escape.SLDPRT", false)]
    public void Only_parts_under_the_root_are_scratch_parts(string relative, bool expected)
    {
        Assert.Equal(expected, LiveScratch.IsScratchPart(Path.Combine(LiveScratch.Root, relative)));
    }

    [Fact]
    public void A_part_elsewhere_is_not_a_scratch_part()
    {
        Assert.False(LiveScratch.IsScratchPart(@"C:\work\owner.SLDPRT"));
        Assert.False(LiveScratch.IsScratchPart(LiveScratch.Root + @"-other\x.SLDPRT"));
    }
}
