using System.Text.RegularExpressions;
using Xunit;

namespace SwAgent.Live.Tests;

public class LiveRunTests
{
    sealed class NamedRun : LiveRun
    {
        public NamedRun(string name)
            : base(name)
        {
        }
    }

    [SkippableFact]
    public void Run_ids_are_unique_to_the_millisecond_and_name_their_class()
    {
        Skip.If(LiveSettings.Live, "SWMCP_LIVE is set: constructing a run would set Diagnostics");
        using var suite = new NamedRun("fault-suite");
        using var canary = new NamedRun("canary");
        Assert.Matches(new Regex(@"^\d{8}-\d{6}-\d{3}-fault-suite$"), suite.Id);
        Assert.EndsWith("-canary", canary.Id);
        Assert.NotEqual(suite.Directory, canary.Directory);
        Assert.NotEqual(suite.Guard.ScratchDirectory, canary.Guard.ScratchDirectory);
    }

    [Theory]
    [InlineData("0.1.0+d820bb98dd71e7617b15c123b91966d49792a0e4", "d820bb98dd71e7617b15c123b91966d49792a0e4")]
    [InlineData("0.1.0", null)]
    public void The_commit_falls_back_to_the_versions_sha(string version, string? expected)
    {
        Assert.Equal(expected, LiveRun.CommitOf(version));
    }
}
