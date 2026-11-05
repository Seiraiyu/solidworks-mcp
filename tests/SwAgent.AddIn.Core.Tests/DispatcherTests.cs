using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DispatcherTests
{
    static Dispatcher Create(UiThread ui, OperationRegistry registry, MemoryLog log) =>
        ui.Invoke(() => new Dispatcher(new RequestExecutor(registry, log), log));

    [Fact]
    public void Runs_requests_on_the_UI_thread_in_order()
    {
        using var ui = new UiThread();
        var log = new MemoryLog();
        var dispatcher = Create(ui, TestOps.Registry(), log);
        var reply = new CapturingReply();
        for (int i = 1; i <= 3; i++) dispatcher.Enqueue(Requests.Echo(i, i.ToString()), DateTime.UtcNow, reply);
        for (int i = 1; i <= 3; i++)
        {
            var result = (JsonObject)reply.Next().Result!;
            Assert.Equal(i.ToString(), result["text"]);
            Assert.Equal((long)ui.ThreadId, result["thread_id"]);
        }
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Hands_control_back_to_the_message_loop_between_requests()
    {
        using var ui = new UiThread();
        var order = new ConcurrentQueue<string>();
        var secondQueued = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(
            echo: (r, c) =>
            {
                order.Enqueue("first");
                firstStarted.Set();
                secondQueued.Wait(TimeSpan.FromSeconds(10));
                SynchronizationContext.Current!.Post(_ => order.Enqueue("message loop"), null);
                return new EchoResult { Text = r.Text };
            },
            other: (r, c) => { order.Enqueue("second"); return new EchoResult { Text = "other" }; });
        var dispatcher = Create(ui, registry, new MemoryLog());
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Other(2), DateTime.UtcNow, reply);
        secondQueued.Set();
        reply.Next();
        reply.Next();
        Assert.Equal(new[] { "first", "message loop", "second" }, order.ToArray());
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Never_nests_a_request_inside_another_even_when_one_pumps_messages()
    {
        using var ui = new UiThread();
        var order = new ConcurrentQueue<string>();
        var secondQueued = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(
            echo: (r, c) =>
            {
                order.Enqueue("first start");
                firstStarted.Set();
                secondQueued.Wait(TimeSpan.FromSeconds(10));
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(5); }
                order.Enqueue("first end");
                return new EchoResult { Text = r.Text };
            },
            other: (r, c) => { order.Enqueue("second"); return new EchoResult { Text = "other" }; });
        var dispatcher = Create(ui, registry, new MemoryLog());
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Other(2), DateTime.UtcNow, reply);
        secondQueued.Set();
        reply.Next();
        reply.Next();
        Assert.Equal(new[] { "first start", "first end", "second" }, order.ToArray());
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Reports_the_running_operation_and_the_queue()
    {
        using var ui = new UiThread();
        Dispatcher? dispatcher = null;
        string? method = null;
        int queued = -1;
        DateTime? started = null;
        var othersQueued = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(echo: (r, c) =>
        {
            firstStarted.Set();
            othersQueued.Wait(TimeSpan.FromSeconds(10));
            method = dispatcher!.CurrentMethod;
            started = dispatcher.CurrentStartedUtc;
            queued = dispatcher.QueuedRequests;
            return new EchoResult { Text = r.Text };
        });
        dispatcher = Create(ui, registry, new MemoryLog());
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Other(2), DateTime.UtcNow, reply);
        dispatcher.Enqueue(Requests.Other(3), DateTime.UtcNow, reply);
        othersQueued.Set();
        for (int i = 0; i < 3; i++) reply.Next();
        Assert.Equal("test.echo", method);
        Assert.NotNull(started);
        Assert.Equal(2, queued);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Drops_requests_whose_client_has_gone()
    {
        using var ui = new UiThread();
        var log = new MemoryLog();
        int otherRuns = 0;
        var release = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(
            echo: (r, c) => { firstStarted.Set(); release.Wait(TimeSpan.FromSeconds(10)); return new EchoResult { Text = r.Text }; },
            other: (r, c) => { Interlocked.Increment(ref otherRuns); return new EchoResult { Text = "other" }; });
        var dispatcher = Create(ui, registry, log);
        var live = new CapturingReply();
        var gone = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, live);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Other(2), DateTime.UtcNow, gone);
        gone.Open = false;
        release.Set();
        live.Next();
        log.WaitFor("request 2 test.other dropped");
        Assert.Equal(0, otherRuns);
        Assert.Empty(gone.Lines);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Dispose_answers_queued_and_later_requests_with_addin_not_loaded()
    {
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        var reply = new CapturingReply();
        ui.Invoke(() =>
        {
            dispatcher.Enqueue(Requests.Echo(1, "queued"), DateTime.UtcNow, reply);
            dispatcher.Dispose();
        });
        Assert.Equal(ErrorCode.AddinNotLoaded, reply.Next().Error!.Code);
        dispatcher.Enqueue(Requests.Echo(2, "late"), DateTime.UtcNow, reply);
        var late = reply.Next();
        Assert.Equal(2L, late.Id);
        Assert.Equal(ErrorCode.AddinNotLoaded, late.Error!.Code);
    }

    [Fact]
    public void A_request_whose_deadline_passes_in_the_queue_is_answered_timeout_without_running()
    {
        using var ui = new UiThread();
        int otherRuns = 0;
        var release = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(
            echo: (r, c) => { firstStarted.Set(); release.Wait(TimeSpan.FromSeconds(10)); return new EchoResult { Text = r.Text }; },
            other: (r, c) => { Interlocked.Increment(ref otherRuns); return new EchoResult { Text = "other" }; });
        var dispatcher = Create(ui, registry, new MemoryLog());
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "slow"), DateTime.UtcNow, reply);
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Other(2, deadlineMs: 50), DateTime.UtcNow, reply);
        Thread.Sleep(300);
        release.Set();

        Assert.True(reply.Next().IsSuccess);
        var late = reply.Next();
        Assert.Equal(2L, late.Id);
        Assert.Equal(ErrorCode.Timeout, late.Error!.Code);
        Assert.True(late.Error.Details!["started"] is false);
        Assert.True((long)late.Error.Details["waited_ms"]! >= 50);
        Assert.Equal(0, otherRuns);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Failures_are_mapped_to_typed_errors_and_the_dispatcher_keeps_serving()
    {
        using var ui = new UiThread();
        var registry = TestOps.Registry(
            echo: (r, c) => r.Text switch
            {
                "throw" => throw new InvalidOperationException("boom"),
                "typed" => throw new SwAgentException(ErrorCode.WrongDocumentType, "not a part"),
                _ => new EchoResult { Text = r.Text },
            },
            verify: (r, res, c) =>
            {
                if (r.Text == "unverified") throw new InvalidOperationException("the feature is missing");
            });
        var dispatcher = Create(ui, registry, new MemoryLog());
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "throw"), DateTime.UtcNow, reply);
        dispatcher.Enqueue(Requests.Echo(2, "typed"), DateTime.UtcNow, reply);
        dispatcher.Enqueue(Requests.Echo(3, "unverified"), DateTime.UtcNow, reply);
        dispatcher.Enqueue(Requests.Echo(4, "fine"), DateTime.UtcNow, reply);

        var thrown = reply.Next();
        Assert.Equal(ErrorCode.OperationFailed, thrown.Error!.Code);
        Assert.Contains("boom", thrown.Error.Message);
        Assert.Equal(ErrorCode.WrongDocumentType, reply.Next().Error!.Code);
        Assert.Equal(ErrorCode.VerificationFailed, reply.Next().Error!.Code);
        var fine = reply.Next();
        Assert.True(fine.IsSuccess);
        Assert.Equal("fine", ((JsonObject)fine.Result!)["text"]);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Must_be_created_on_a_thread_with_a_message_loop_it_keeps()
    {
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        Assert.Equal(ui.ThreadId, dispatcher.UiThreadId);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Dispose());
        ui.Invoke(dispatcher.Dispose);
    }
}
