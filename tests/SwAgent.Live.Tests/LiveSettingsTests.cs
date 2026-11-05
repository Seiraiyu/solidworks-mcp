using System.Collections.Generic;
using Xunit;

namespace SwAgent.Live.Tests;

public class LiveSettingsTests
{
    static System.Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, value) in values) map[name] = value;
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void Off_unless_SWMCP_LIVE_is_1()
    {
        Assert.False(LiveSettings.LiveEnabled(Env()));
        Assert.False(LiveSettings.LiveEnabled(Env(("SWMCP_LIVE", "true"))));
        Assert.True(LiveSettings.LiveEnabled(Env(("SWMCP_LIVE", "1"))));
    }

    [Fact]
    public void The_canary_needs_both_switches()
    {
        Assert.False(LiveSettings.CanaryEnabled(Env(("SWMCP_CANARY", "1"))));
        Assert.False(LiveSettings.CanaryEnabled(Env(("SWMCP_LIVE", "1"))));
        Assert.True(LiveSettings.CanaryEnabled(Env(("SWMCP_LIVE", "1"), ("SWMCP_CANARY", "1"))));
    }

    [SkippableFact]
    public void A_live_case_is_skipped_by_default()
    {
        Skip.If(LiveSettings.Live, "SWMCP_LIVE is set: this check is for the default run");
        Assert.Throws<SkipException>(LiveSettings.RequireLive);
        Assert.Throws<SkipException>(LiveSettings.RequireCanary);
    }
}
