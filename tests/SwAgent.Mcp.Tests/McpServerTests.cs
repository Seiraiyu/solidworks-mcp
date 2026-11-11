using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class McpServerTests
{
    static ClientOptions NoSolidWorks() => new ClientOptions { Processes = new FakeProcesses(), Pipes = new FakePipes(_ => false) };

    /// <summary>A fake add-in that never answers dialog.respond, so a call to it stays in flight.</summary>
    static FakeAddin Stalling() => new FakeAddin(r => r.Method == "dialog.respond" ? null : RpcCodec.Success(r.Id, JsonCodec.ToJson(FakeAddin.SampleStatus())));

    [Fact]
    public async Task Lists_every_tool_each_with_its_contract_schema_and_deadline_ms()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        Assert.Equal(McpHost.ServerName, server.Client.ServerInfo!.Name);
        var tools = (await server.Client.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Equal(ToolCatalog.All.Select(t => t.Name).ToArray(), tools.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal(ServerToolCatalog.Find("sw_status")!.Description, tools["sw_status"].Description);
        Assert.Equal(Json.Write(SwTool.WithDeadline(JsonSchema.For(typeof(SwStatusRequest)))), Json.Write(Json.Parse(tools["sw_status"].JsonSchema.GetRawText())));
        Assert.Equal(Json.Write(SwTool.WithDeadline(JsonSchema.For(typeof(DialogRespondRequest)))), Json.Write(Json.Parse(tools["sw_dialog_respond"].JsonSchema.GetRawText())));
        foreach (var tool in tools.Values)
        {
            var properties = (JsonObject)((JsonObject)Json.Parse(tool.JsonSchema.GetRawText())!)["properties"]!;
            Assert.Equal("integer", ((JsonObject)properties["deadline_ms"]!)["type"]);
        }
        Assert.True(tools["sw_status"].ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.True(tools["sw_events"].ProtocolTool.Annotations!.ReadOnlyHint);
        Assert.False(tools["sw_dialog_respond"].ProtocolTool.Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Sw_status_composes_the_state_the_addin_state_and_the_session()
    {
        using var addin = new FakeAddin();
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.StatusAsync();
        Assert.False(isError);
        Assert.Equal("ready", body["state"]);
        Assert.Equal(true, body["responding"]);
        Assert.Equal("0.1.0-fake", ((JsonObject)body["addin"]!)["addin_version"]);
        Assert.Equal("Part1", ((JsonObject)((JsonObject)body["session"]!)["active_document"]!)["title"]);
        addin.Next("session.status");
    }

    [Fact]
    public async Task Without_SOLIDWORKS_sw_status_answers_not_running_rather_than_failing()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        var (isError, body) = await server.StatusAsync();
        Assert.False(isError);
        Assert.Equal("not_running", body["state"]);
        Assert.Contains("3DEXPERIENCE dashboard", (string)body["message"]!);
        Assert.Equal("sw_not_running", ((JsonObject)body["error"]!)["code"]);
    }

    [Fact]
    public async Task Sw_status_answers_while_another_tool_call_waits_on_SOLIDWORKS()
    {
        using var addin = Stalling();
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var waiting = server.CallAsync("sw_dialog_respond", new Dictionary<string, object?> { ["dialog"] = "d1-1", ["button"] = "OK", ["deadline_ms"] = 30000 });
        addin.Next("dialog.respond");
        var clock = Stopwatch.StartNew();
        var (isError, body) = await server.StatusAsync();
        Assert.False(isError);
        Assert.Equal("ready", body["state"]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);
    }

    [Fact]
    public async Task Sw_dialog_respond_runs_dialog_respond_with_the_deadline_given()
    {
        using var addin = new FakeAddin(r => RpcCodec.Success(r.Id, JsonCodec.ToJson(new DialogRespondResult { Dialog = "d1-1", Pressed = "OK", ControlId = 1, Outcome = DialogOutcome.NoRequest })));
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.CallAsync("sw_dialog_respond", new Dictionary<string, object?> { ["dialog"] = "d1-1", ["button"] = "OK", ["deadline_ms"] = 1234 });
        Assert.False(isError);
        Assert.Equal("no_request", body["outcome"]);
        var request = addin.Next("dialog.respond");
        Assert.Equal(1234L, request.DeadlineMs);
        Assert.False(request.Params.ContainsKey("deadline_ms"));
        Assert.Equal("OK", request.Params["button"]);
    }

    [Fact]
    public async Task A_deadline_ms_out_of_range_is_invalid_argument()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        var (isError, body) = await server.CallAsync("sw_status", new Dictionary<string, object?> { ["deadline_ms"] = 0 });
        Assert.True(isError);
        Assert.Equal("invalid_argument", body["code"]);
    }

    [Fact]
    public async Task A_typed_error_from_the_addin_is_an_error_result()
    {
        using var addin = new FakeAddin(r => RpcCodec.Failure(r.Id, RpcErrorCodes.ServerError,
            new SwAgentError(ErrorCode.InvalidArgument, "no open dialog 'd9-9'")));
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.CallAsync("sw_dialog_respond", new Dictionary<string, object?> { ["dialog"] = "d9-9", ["button"] = "OK" });
        Assert.True(isError);
        Assert.Equal("invalid_argument", body["code"]);
    }

    [Fact]
    public async Task Sw_events_returns_the_events_received_after_a_cursor()
    {
        using var addin = new FakeAddin();
        await using var server = await InProcessServer.StartAsync(addin.Options());
        await server.StatusAsync();
        await addin.SendEventAsync(1, "document.saved", new DocumentEvent { Title = "Part1" });
        await addin.SendEventAsync(2, "document.closed", new DocumentEvent { Title = "Part1" });
        for (int i = 0; i < 250 && server.Supervisor.Events.LastSeq < 2; i++) await Task.Delay(20);

        var (isError, body) = await server.CallAsync("sw_events", new Dictionary<string, object?> { ["after_seq"] = 1 });
        Assert.False(isError);
        var e = (JsonObject)Assert.Single((List<object?>)body["events"]!)!;
        Assert.Equal("document.closed", e["kind"]);
        Assert.Equal(2L, body["last_seq"]);
        Assert.Equal(false, body["missed"]);
    }

    [Fact]
    public async Task Sw_reconnect_answers_with_the_status()
    {
        using var addin = new FakeAddin();
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.CallAsync("sw_reconnect", new Dictionary<string, object?> { ["process_id"] = Environment.ProcessId });
        Assert.False(isError);
        Assert.Equal("ready", body["state"]);
        Assert.Equal((long)Environment.ProcessId, body["process_id"]);
    }

    [Fact]
    public async Task A_server_bug_is_a_protocol_error_and_is_logged()
    {
        var log = new CapturingLogger();
        var options = new ClientOptions { Processes = new BrokenProcesses(), Pipes = new FakePipes(_ => false) };
        await using var server = await InProcessServer.StartAsync(options, log);
        var e = await Assert.ThrowsAsync<McpProtocolException>(() =>
            server.Client.CallToolAsync("sw_status", new Dictionary<string, object?>()).AsTask());
        Assert.Equal(McpErrorCode.InternalError, e.ErrorCode);
        Assert.DoesNotContain("exploded", e.Message);
        var error = Assert.Single(log.Entries, l => l.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);
        Assert.Contains("sw_status", error.Message);
    }

    [Fact]
    public async Task Logs_each_call_with_its_outcome_and_the_params_at_debug_only()
    {
        var log = new CapturingLogger();
        using var addin = new FakeAddin(r => RpcCodec.Failure(r.Id, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.InvalidArgument, "no such dialog")));
        await using var server = await InProcessServer.StartAsync(addin.Options(), log);
        Assert.False((await server.StatusAsync()).IsError);
        Assert.True((await server.CallAsync("sw_dialog_respond", new Dictionary<string, object?> { ["dialog"] = "d1-1", ["button"] = "OK" })).IsError);

        var info = log.Entries.Where(l => l.Level == LogLevel.Information).Select(l => l.Message).ToList();
        Assert.Equal(2, info.Count);
        Assert.Matches(@"^sw_status: ok in \d+ ms$", info[0]);
        Assert.Matches(@"^sw_dialog_respond dialog\.respond: invalid_argument in \d+ ms$", info[1]);
        var debug = log.Entries.Where(l => l.Level == LogLevel.Debug).Select(l => l.Message).ToList();
        Assert.Equal(new[] { "sw_status params {}", "sw_dialog_respond params {\"dialog\":\"d1-1\",\"button\":\"OK\"}" }, debug);
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("loud", LogLevel.Information)]
    public void SWMCP_LOG_LEVEL_sets_the_minimum_level(string? value, LogLevel expected)
    {
        Assert.Equal(expected, McpHost.MinimumLevel(value));
    }

    [Fact]
    public async Task Connects_once_and_reconnects_after_the_addin_drops()
    {
        using var first = new FakeAddin();
        var options = first.Options();
        await using var server = await InProcessServer.StartAsync(options);
        Assert.False(server.Supervisor.IsConnected);
        Assert.False((await server.StatusAsync()).IsError);
        Assert.False((await server.StatusAsync()).IsError);
        Assert.Equal("session.hello", first.Next().Method);
        Assert.DoesNotContain("session.hello", Enumerable.Range(0, 5).Select(_ => first.Next().Method));

        first.Disconnect();
        for (int i = 0; i < 100 && server.Supervisor.IsConnected; i++) await Task.Delay(50);
        Assert.False(server.Supervisor.IsConnected);

        using var second = new FakeAddin();
        options.Pipes = new FakePipes(n => n == second.PipeName);
        options.PipeNameFor = _ => second.PipeName;
        Assert.Equal("ready", (await server.StatusAsync()).Body["state"]);
        Assert.Equal("session.hello", second.Next().Method);
    }

    [Fact]
    public void Every_tool_has_an_MCP_name_and_a_contract()
    {
        var names = new Regex("^[a-zA-Z0-9_-]{1,64}$");
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Matches(names, tool.Name);
            if (tool.Operation != null) Assert.NotNull(OperationCatalog.Find(tool.Operation));
            else Assert.NotNull(ServerToolCatalog.Find(tool.Name));
        }
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        Assert.Equal(ServerToolCatalog.All.Select(t => t.Name).OrderBy(n => n), ToolCatalog.All.Where(t => t.Operation == null).Select(t => t.Name).OrderBy(n => n));
    }

    [Fact]
    public void Every_operation_that_is_not_internal_is_a_tool_named_from_it()
    {
        var expected = OperationCatalog.All.Where(o => !o.Internal).Select(o => o.ToolName)
            .Concat(ServerToolCatalog.All.Select(t => t.Name)).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, ToolCatalog.All.Select(t => t.Name));
        foreach (var tool in ToolCatalog.All.Where(t => t.Operation != null))
        {
            var op = OperationCatalog.Find(tool.Operation!)!;
            Assert.Equal(op.ToolName, tool.Name);
            Assert.Equal(op.ReadOnly, tool.ReadOnly);
        }
        Assert.True(ToolCatalog.All.Single(t => t.Name == "sw_status").ReadOnly);
        Assert.True(ToolCatalog.All.Single(t => t.Name == "sw_events").ReadOnly);
        Assert.False(ToolCatalog.All.Single(t => t.Name == "sw_reconnect").ReadOnly);
    }

    [Fact]
    public void Build_leaves_out_internal_operations()
    {
        var ops = new[]
        {
            new OperationDescriptor("model.mass", "model", typeof(SwStatusRequest), typeof(SwStatusResult), "", readOnly: true),
            new OperationDescriptor("diag.thing", "diag", typeof(SwStatusRequest), typeof(SwStatusResult), "", isInternal: true),
        };
        var tools = ToolCatalog.Build(Array.Empty<ServerToolDescriptor>(), ops);
        Assert.Equal(new[] { "sw_model_mass" }, tools.Select(t => t.Name).ToArray());
        Assert.True(tools[0].ReadOnly);
    }

    [Fact]
    public void The_escape_hatch_is_a_tool_unless_it_is_turned_off()
    {
        Assert.Contains(ToolCatalog.For(true), t => t.Name == "sw_api_call" && t.Operation == "api.call" && !t.ReadOnly);
        Assert.Equal(ToolCatalog.All, ToolCatalog.For(true));
        var off = ToolCatalog.For(false);
        Assert.DoesNotContain(off, t => t.Name == "sw_api_call");
        Assert.Equal(ToolCatalog.All.Count - 1, off.Count);
    }

    [Fact]
    public async Task Turned_off_the_server_lists_no_sw_api_call_and_the_supervisor_refuses_api_call()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks(), apiCall: false);
        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain("sw_api_call", tools);
        Assert.Contains("sw_export_part", tools);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => server.Supervisor.CallAsync("api.call", new JsonObject { ["target"] = "app", ["member"] = "RevisionNumber" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("turned off", e.Message);
    }

    [Fact]
    public async Task On_by_default_the_server_lists_sw_api_call_with_its_units_first()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "sw_api_call");
        Assert.StartsWith("SOLIDWORKS' own units: metres and radians", tool.Description);
    }

    [Fact]
    public void No_internal_operation_is_a_tool()
    {
        foreach (var tool in ToolCatalog.All.Where(t => t.Operation != null))
            Assert.False(OperationCatalog.Find(tool.Operation!)!.Internal, $"{tool.Name} exposes internal operation {tool.Operation}");
    }
}
