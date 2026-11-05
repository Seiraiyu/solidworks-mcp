using System;
using System.Collections.Generic;
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

    [Fact]
    public async Task Lists_sw_status_with_the_contract_schema()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        Assert.Equal(McpHost.ServerName, server.Client.ServerInfo!.Name);
        var tool = Assert.Single(await server.Client.ListToolsAsync());
        Assert.Equal("sw_status", tool.Name);
        Assert.Equal(OperationCatalog.Find("session.status")!.Description, tool.Description);
        Assert.Equal(Json.Write(JsonSchema.For(typeof(SessionStatusRequest))), Json.Write(Json.Parse(tool.JsonSchema.GetRawText())));
        Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
    }

    [Fact]
    public async Task Sw_status_returns_the_session_status()
    {
        using var addin = new FakeAddin();
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.StatusAsync();
        Assert.False(isError);
        Assert.Equal("34.4.0", body["sw_revision"]);
        Assert.Equal("Part1", ((JsonObject)body["active_document"]!)["title"]);
        Assert.Equal("session.hello", addin.Next().Method);
        Assert.Equal("session.status", addin.Next().Method);
    }

    [Fact]
    public async Task Without_SOLIDWORKS_sw_status_is_an_error_result_with_its_code()
    {
        await using var server = await InProcessServer.StartAsync(NoSolidWorks());
        var (isError, body) = await server.StatusAsync();
        Assert.True(isError);
        Assert.Equal("sw_not_running", body["code"]);
        Assert.Contains("3DEXPERIENCE launcher", (string)body["message"]!);
    }

    [Fact]
    public async Task A_typed_error_from_the_addin_is_an_error_result()
    {
        using var addin = new FakeAddin(r => RpcCodec.Failure(r.Id, RpcErrorCodes.ServerError,
            new SwAgentError(ErrorCode.VerificationFailed, "the active document changed")));
        await using var server = await InProcessServer.StartAsync(addin.Options());
        var (isError, body) = await server.StatusAsync();
        Assert.True(isError);
        Assert.Equal("verification_failed", body["code"]);
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
        using var addin = new FakeAddin();
        await using var server = await InProcessServer.StartAsync(addin.Options(), log);
        Assert.False((await server.StatusAsync()).IsError);
        await using var down = await InProcessServer.StartAsync(NoSolidWorks(), log);
        Assert.True((await down.StatusAsync()).IsError);

        var info = log.Entries.Where(l => l.Level == LogLevel.Information).Select(l => l.Message).ToList();
        Assert.Equal(2, info.Count);
        Assert.Matches(@"^sw_status session\.status: ok in \d+ ms$", info[0]);
        Assert.Matches(@"^sw_status session\.status: sw_not_running in \d+ ms$", info[1]);
        var debug = log.Entries.Where(l => l.Level == LogLevel.Debug).Select(l => l.Message).ToList();
        Assert.Equal(2, debug.Count);
        Assert.All(debug, m => Assert.Equal("sw_status params {}", m));
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
        Assert.False(server.Session.IsConnected);
        Assert.False((await server.StatusAsync()).IsError);
        Assert.False((await server.StatusAsync()).IsError);
        Assert.Equal(new[] { "session.hello", "session.status", "session.status" }, new[] { first.Next().Method, first.Next().Method, first.Next().Method });

        first.Disconnect();
        for (int i = 0; i < 100 && server.Session.IsConnected; i++) await Task.Delay(50);
        Assert.False(server.Session.IsConnected);

        using var second = new FakeAddin();
        options.Pipes = new FakePipes(n => n == second.PipeName);
        options.PipeNameFor = _ => second.PipeName;
        Assert.False((await server.StatusAsync()).IsError);
        Assert.Equal("session.hello", second.Next().Method);
    }

    [Fact]
    public void Every_tool_has_an_MCP_name_and_a_contract()
    {
        var names = new Regex("^[a-zA-Z0-9_-]{1,64}$");
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Matches(names, tool.Name);
            Assert.NotNull(OperationCatalog.Find(tool.Operation));
        }
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        Assert.DoesNotContain(ToolCatalog.All, t => t.Operation == "session.hello");
    }
}
