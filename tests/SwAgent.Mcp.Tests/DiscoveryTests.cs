using System;
using System.Collections.Generic;
using System.IO.Pipes;
using SwAgent.Client;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class DiscoveryTests
{
    static AddinLocator Locator(int[] ids, Func<string, bool>? pipe = null) =>
        new AddinLocator(new FakeProcesses(ids), new FakePipes(pipe ?? (_ => true)));

    [Fact]
    public void No_SOLIDWORKS_is_sw_not_running()
    {
        var e = Assert.Throws<SwAgentException>(() => Locator(Array.Empty<int>()).Locate(null));
        Assert.Equal(ErrorCode.SwNotRunning, e.Code);
        Assert.Contains("3DEXPERIENCE launcher", e.Message);
    }

    [Fact]
    public void Two_instances_without_a_pid_is_sw_multiple_instances_listing_them()
    {
        var e = Assert.Throws<SwAgentException>(() => Locator(new[] { 10, 20 }).Locate(null));
        Assert.Equal(ErrorCode.SwMultipleInstances, e.Code);
        Assert.Equal(new List<object?> { 10L, 20L }, e.Error.Details!["process_ids"]);
    }

    [Fact]
    public void A_pid_picks_one_of_several()
    {
        Assert.Equal(20, Locator(new[] { 10, 20 }).Locate(20));
    }

    [Fact]
    public void A_pid_that_is_not_running_is_sw_not_running()
    {
        var e = Assert.Throws<SwAgentException>(() => Locator(new[] { 10 }).Locate(30));
        Assert.Equal(ErrorCode.SwNotRunning, e.Code);
    }

    [Fact]
    public void SOLIDWORKS_without_the_pipe_is_addin_not_loaded()
    {
        var e = Assert.Throws<SwAgentException>(() => Locator(new[] { 10 }, _ => false).Locate(null));
        Assert.Equal(ErrorCode.AddinNotLoaded, e.Code);
        Assert.Contains("swmcp addin register", e.Message);
    }

    [Fact]
    public void The_only_instance_with_its_pipe_is_found()
    {
        string? asked = null;
        Assert.Equal(10, Locator(new[] { 10 }, n => { asked = n; return true; }).Locate(null));
        Assert.Equal("solidworks-mcp-10", asked);
    }

    [Fact]
    public void The_pipe_directory_sees_a_live_pipe_and_not_a_missing_one()
    {
        string name = "swagent-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.True(SystemPipeDirectory.Instance.Exists(name));
        Assert.False(SystemPipeDirectory.Instance.Exists(name + "-missing"));
    }
}
