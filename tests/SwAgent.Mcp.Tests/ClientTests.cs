using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class ClientTests
{
    [Fact]
    public async Task Connecting_says_hello_first_with_the_protocol_version()
    {
        using var addin = new FakeAddin();
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        var hello = addin.Next();
        Assert.Equal("session.hello", hello.Method);
        Assert.Equal((long)Protocol.Version, hello.Params["protocol_version"]);
        Assert.Equal("0.1.0-fake", client.Hello.AddinVersion);
        Assert.Equal(Environment.ProcessId, client.ProcessId);
    }

    [Fact]
    public async Task A_typed_call_round_trips()
    {
        using var addin = new FakeAddin();
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        var status = await client.CallAsync<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), TimeSpan.FromSeconds(5));
        Assert.Equal("Part1", status.ActiveDocument!.Title);
        addin.Next();
        var request = addin.Next();
        Assert.Equal("session.status", request.Method);
        Assert.Equal(5000L, request.DeadlineMs);
    }

    [Fact]
    public async Task A_protocol_mismatch_is_addin_incompatible_naming_both_versions()
    {
        using var addin = new FakeAddin(protocolVersion: Protocol.Version + 1);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => SwAgentClient.ConnectAsync(addin.Options()));
        Assert.Equal(ErrorCode.AddinIncompatible, e.Code);
        Assert.Equal((long)Protocol.Version, e.Error.Details!["client_protocol_version"]);
        Assert.Equal((long)Protocol.Version + 1, e.Error.Details["addin_protocol_version"]);
        Assert.Equal("0.1.0-fake", e.Error.Details["addin_version"]);
    }

    [Fact]
    public async Task A_typed_error_from_the_addin_is_thrown_with_its_code()
    {
        using var addin = new FakeAddin(r => RpcCodec.Failure(r.Id, RpcErrorCodes.ServerError,
            new SwAgentError(ErrorCode.WrongDocumentType, "not a part", new JsonObject { ["type"] = "drawing" })));
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        var e = await Assert.ThrowsAsync<SwAgentException>(() => client.CallAsync("session.status", new JsonObject()));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("drawing", e.Error.Details!["type"]);
    }

    [Fact]
    public async Task No_answer_by_the_deadline_is_timeout_and_the_late_answer_is_dropped()
    {
        var silent = new ConcurrentQueue<RpcRequest>();
        using var addin = new FakeAddin(r =>
        {
            if (r.Params.ContainsKey("slow")) { silent.Enqueue(r); return null; }
            return RpcCodec.Success(r.Id, JsonCodec.ToJson(FakeAddin.SampleStatus()));
        });
        using var client = await SwAgentClient.ConnectAsync(addin.Options());

        var e = await Assert.ThrowsAsync<SwAgentException>(() =>
            client.CallAsync("session.status", new JsonObject { ["slow"] = true }, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(ErrorCode.Timeout, e.Code);
        Assert.True(e.Error.Details!.ContainsKey("started"));
        Assert.Null(e.Error.Details["started"]);

        Assert.True(silent.TryDequeue(out var late));
        await addin.SendAsync(RpcCodec.Success(late!.Id, new JsonObject { ["late"] = true }));
        var next = (JsonObject)(await client.CallAsync("session.status", new JsonObject(), TimeSpan.FromSeconds(5)))!;
        Assert.Equal("Part1", ((JsonObject)next["active_document"]!)["title"]);
    }

    [Fact]
    public async Task A_dropped_connection_fails_pending_and_later_calls_with_addin_not_loaded()
    {
        using var addin = new FakeAddin(r => null);
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        var pending = client.CallAsync("session.status", new JsonObject(), TimeSpan.FromSeconds(30));
        addin.Next();
        addin.Next();
        addin.Disconnect();
        var e = await Assert.ThrowsAsync<SwAgentException>(() => pending);
        Assert.Equal(ErrorCode.AddinNotLoaded, e.Code);
        Assert.False(client.IsConnected);
        var again = await Assert.ThrowsAsync<SwAgentException>(() => client.CallAsync("session.status", new JsonObject()));
        Assert.Equal(ErrorCode.AddinNotLoaded, again.Code);
    }

    [Fact]
    public async Task A_busy_addin_refuses_the_connection()
    {
        using var addin = new FakeAddin(busy: true);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => SwAgentClient.ConnectAsync(addin.Options()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal(1L, e.Error.Details!["holder_process_id"]);
    }

    /// <summary>The busy line can close the pipe while hello is still being written; that write is aborted, and must still read busy.</summary>
    [Fact]
    public async Task A_busy_addin_refuses_every_connection_even_when_it_closes_mid_write()
    {
        for (int i = 0; i < 200; i++)
        {
            using var addin = new FakeAddin(busy: true);
            var e = await Assert.ThrowsAsync<SwAgentException>(() => SwAgentClient.ConnectAsync(addin.Options()));
            Assert.Equal(ErrorCode.Busy, e.Code);
        }
    }

    [Fact]
    public async Task Notifications_go_to_the_handler_not_to_a_call()
    {
        var seen = new TaskCompletionSource<(string, JsonObject)>();
        using var addin = new FakeAddin();
        var options = addin.Options();
        options.OnNotification = (method, p) => seen.TrySetResult((method, p));
        using var client = await SwAgentClient.ConnectAsync(options);
        await addin.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"event.test\",\"params\":{\"n\":1}}");
        var (name, parameters) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("event.test", name);
        Assert.Equal(1L, parameters["n"]);
        Assert.NotNull(await client.CallAsync("session.status", new JsonObject()));
    }

    [Fact]
    public async Task A_pipe_served_by_another_process_is_refused()
    {
        using var addin = new FakeAddin();
        var options = addin.Options();
        options.Processes = new FakeProcesses(Environment.ProcessId + 100000);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => SwAgentClient.ConnectAsync(options));
        Assert.Equal(ErrorCode.AddinNotLoaded, e.Code);
        Assert.Equal((long)Environment.ProcessId, e.Error.Details!["server_process_id"]);
    }

    [Fact]
    public async Task A_deadline_out_of_range_is_invalid_argument()
    {
        using var addin = new FakeAddin();
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        var e = await Assert.ThrowsAsync<SwAgentException>(() => client.CallAsync("session.status", new JsonObject(), TimeSpan.FromDays(30)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
    }

    [Fact]
    public async Task A_call_with_no_deadline_uses_its_familys_default()
    {
        using var addin = new FakeAddin();
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        Assert.Equal(10_000L, addin.Next().DeadlineMs); // session.hello: the session family
        await client.CallAsync("session.status", new JsonObject());
        Assert.Equal(10_000L, addin.Next().DeadlineMs);
    }

    [Fact]
    public async Task A_deadline_over_one_hour_is_refused_before_it_is_sent()
    {
        using var addin = new FakeAddin();
        using var client = await SwAgentClient.ConnectAsync(addin.Options());
        addin.Next();
        var e = await Assert.ThrowsAsync<SwAgentException>(() => client.CallAsync("session.status", new JsonObject(), TimeSpan.FromHours(2)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.False(addin.Received.TryTake(out _, 300));
    }
}
