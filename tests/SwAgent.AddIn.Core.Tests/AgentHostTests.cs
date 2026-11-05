using System;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.AddIn.Core.Pipe;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class AgentHostTests
{
    [Fact]
    public async Task Serves_an_operation_over_the_pipe_on_the_UI_thread()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var host = ui.Invoke(() => AgentHost.Start(name, TestOps.Registry(), new MemoryLog()));
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(Requests.Echo(1, "hello")));
            var response = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
            var result = (JsonObject)response.Result!;
            Assert.Equal("hello", result["text"]);
            Assert.Equal((long)ui.ThreadId, result["thread_id"]);
        }
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task A_malformed_line_is_answered_without_the_UI_thread()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var host = ui.Invoke(() => AgentHost.Start(name, TestOps.Registry(), new MemoryLog()));
        using (var client = new RawClient(name))
        {
            await client.SendAsync("not json");
            var response = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
            Assert.Equal(RpcErrorCodes.ParseError, response.RpcCode);
            Assert.Equal(ErrorCode.InvalidArgument, response.Error!.Code);
        }
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task Requests_queued_by_a_departed_client_are_dropped()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var log = new MemoryLog();
        int otherRuns = 0;
        var release = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(
            echo: (r, c) => { firstStarted.Set(); release.Wait(TimeSpan.FromSeconds(10)); return new EchoResult { Text = r.Text }; },
            other: (r, c) => { Interlocked.Increment(ref otherRuns); return new EchoResult { Text = "other" }; });
        var host = ui.Invoke(() => AgentHost.Start(name, registry, log));
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(Requests.Echo(1, "slow")));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
            await client.SendAsync(RpcCodec.FormatRequest(Requests.Other(2)));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (host.Dispatcher.QueuedRequests == 0 && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.Equal(1, host.Dispatcher.QueuedRequests);
        }
        log.WaitFor("disconnected");
        release.Set();
        log.WaitFor("request 2 test.other dropped");
        Assert.Equal(0, otherRuns);
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task Unloading_answers_queued_requests_then_closes_the_pipe()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        AgentHost? host = null;
        var release = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(echo: (r, c) =>
        {
            firstStarted.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            // Posted now, so it runs before the timer that would start the next request: SOLIDWORKS unloading mid-queue.
            SynchronizationContext.Current!.Post(_ => host!.Dispose(), null);
            return new EchoResult { Text = r.Text };
        });
        host = ui.Invoke(() => AgentHost.Start(name, registry, new MemoryLog()));
        using var client = new RawClient(name);
        await client.SendAsync(RpcCodec.FormatRequest(Requests.Echo(1, "first")));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        await client.SendAsync(RpcCodec.FormatRequest(Requests.Other(2)));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (host.Dispatcher.QueuedRequests == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        release.Set();

        Assert.True(RpcCodec.ParseResponse((await client.ReadLineAsync())!).IsSuccess);
        var second = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
        Assert.Equal(2L, second.Id);
        Assert.Equal(ErrorCode.AddinNotLoaded, second.Error!.Code);
        Assert.Null(await client.ReadLineAsync());
    }

    [Fact]
    public void A_second_host_on_the_same_name_fails_cleanly()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var host = ui.Invoke(() => AgentHost.Start(name, TestOps.Registry(), new MemoryLog()));
        ui.Invoke(() => Assert.Throws<PipeNameInUseException>(() => AgentHost.Start(name, TestOps.Registry(), new MemoryLog())));
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task A_pipe_operation_answers_while_the_UI_thread_is_blocked()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var registry = TestOps.RegistryWithPeek(echo: (r, c) => { started.Set(); release.Wait(TimeSpan.FromSeconds(20)); return new EchoResult { Text = r.Text }; });
        var runtime = new AgentRuntime("t", 1, new MemoryLog());
        var host = ui.Invoke(() => AgentHost.Start(name, registry, runtime));
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(Requests.Echo(1, "block")));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            await client.SendAsync(RpcCodec.FormatRequest(new RpcRequest(2, "test.peek", new JsonObject(), 5000)));
            var peek = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
            Assert.Equal(2L, peek.Id);
            Assert.NotEqual((long)ui.ThreadId, ((JsonObject)peek.Result!)["thread_id"]);
            release.Set();
            Assert.Equal(1L, RpcCodec.ParseResponse((await client.ReadLineAsync())!).Id);
        }
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task The_watchdog_answers_a_running_request_timeout_and_its_end_arrives_as_an_event()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var release = new ManualResetEventSlim(false);
        var registry = TestOps.RegistryWithPeek(echo: (r, c) => { release.Wait(TimeSpan.FromSeconds(20)); return new EchoResult { Text = r.Text }; });
        var host = ui.Invoke(() => AgentHost.Start(name, registry, new AgentRuntime("t", 1, new MemoryLog())));
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(Requests.Echo(1, "slow", deadlineMs: 300)));
            var timeout = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
            Assert.Equal(ErrorCode.Timeout, timeout.Error!.Code);
            release.Set();
            var line = (await client.ReadLineAsync())!;
            var p = (JsonObject)((JsonObject)Json.Parse(line)!)["params"]!;
            Assert.Equal("operation.completed", p["kind"]);
        }
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task Connection_hooks_attach_events_and_post_the_disconnect_action()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var runtime = new AgentRuntime("t", 1, new MemoryLog());
        var disconnected = new ManualResetEventSlim(false);
        int disconnectThread = 0;
        var host = ui.Invoke(() => AgentHost.Start(name, TestOps.RegistryWithPeek(), runtime));
        host.ClientDisconnected = () => { disconnectThread = Thread.CurrentThread.ManagedThreadId; disconnected.Set(); };
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(new RpcRequest(1, "test.peek", new JsonObject(), 5000)));
            await client.ReadLineAsync();
            runtime.Events.Publish("session.closing", new SessionClosingEvent { Reason = "x" });
            var p = (JsonObject)((JsonObject)Json.Parse((await client.ReadLineAsync())!)!)["params"]!;
            Assert.Equal("session.closing", p["kind"]);
        }
        Assert.True(disconnected.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(ui.ThreadId, disconnectThread);
        ui.Invoke(host.Dispose);
    }

    [Fact]
    public async Task A_pipe_operation_that_waits_does_not_hold_up_the_next_line()
    {
        using var ui = new UiThread();
        string name = PipeNames.Unique();
        var gate = new ManualResetEventSlim(false);
        var registry = OperationRegistry.Create(OperationCatalog.FromTypes(new[] { typeof(EchoRequest), typeof(PeekRequest), typeof(WaitRequest) }),
            new IOperation[] { new EchoOperation(), new PeekOperation(), new WaitOperation(gate) });
        var host = ui.Invoke(() => AgentHost.Start(name, registry, new AgentRuntime("t", 1, new MemoryLog())));
        using (var client = new RawClient(name))
        {
            await client.SendAsync(RpcCodec.FormatRequest(new RpcRequest(1, "test.wait", new JsonObject(), 30000)));
            await client.SendAsync(RpcCodec.FormatRequest(new RpcRequest(2, "test.peek", new JsonObject(), 5000)));
            Assert.Equal(2L, RpcCodec.ParseResponse((await client.ReadLineAsync())!).Id);
            gate.Set();
            Assert.Equal(1L, RpcCodec.ParseResponse((await client.ReadLineAsync())!).Id);
        }
        ui.Invoke(host.Dispose);
    }
}
