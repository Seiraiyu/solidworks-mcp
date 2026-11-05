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
}
