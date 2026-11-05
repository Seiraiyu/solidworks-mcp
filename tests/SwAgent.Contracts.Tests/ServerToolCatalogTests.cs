using System;
using System.Linq;
using Xunit;

namespace SwAgent.Contracts.Tests;

[ServerTool("Bad Name", typeof(SwStatusResult))] public sealed class BadToolRequest { }

public class ServerToolCatalogTests
{
    [Fact]
    public void Catalog_holds_the_phase_2C_server_tools()
    {
        Assert.Equal(new[] { "sw_events", "sw_reconnect", "sw_status" }, ServerToolCatalog.All.Select(t => t.Name).ToArray());
        Assert.Equal(typeof(SwStatusResult), ServerToolCatalog.Find("sw_status")!.ResultType);
        Assert.Equal(typeof(SwStatusResult), ServerToolCatalog.Find("sw_reconnect")!.ResultType);
        Assert.Equal(typeof(SwEventsResult), ServerToolCatalog.Find("sw_events")!.ResultType);
        Assert.All(ServerToolCatalog.All, t => Assert.False(string.IsNullOrEmpty(t.Description)));
        Assert.Null(ServerToolCatalog.Find("sw_nope"));
    }

    [Fact]
    public void A_server_tool_name_must_be_sw_and_lower_case_words()
    {
        var e = Assert.Throws<ArgumentException>(() => ServerToolCatalog.FromTypes(new[] { typeof(BadToolRequest) }));
        Assert.Contains("Bad Name", e.Message);
    }

    [Fact]
    public void Supervisor_states_have_snake_case_wire_names()
    {
        Assert.Equal(
            new[] { "not_running", "multiple", "starting", "loading", "ready", "crashed", "addin_unavailable" },
            Enum.GetValues(typeof(SupervisorState)).Cast<SupervisorState>().Select(s => WireNames.ForEnum(s)).ToArray());
    }
}
