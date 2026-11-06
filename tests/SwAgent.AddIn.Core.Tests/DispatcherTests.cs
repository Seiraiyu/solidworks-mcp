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
    public void A_request_whose_deadline_passes_in_the_queue_is_answered_busy_without_running()
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
        Assert.Equal(ErrorCode.Busy, late.Error!.Code);
        var d = late.Error.Details!;
        Assert.True(d["started"] is false);
        Assert.True((long)d["waited_ms"]! >= 50);
        Assert.Equal(0L, d["queued_ahead"]);
        var ranBefore = (JsonObject)d["running"]!;
        Assert.Equal(1L, ranBefore["id"]);
        Assert.Equal("test.echo", ranBefore["method"]);
        Assert.Equal(true, ranBefore["finished"]);
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

    [Fact]
    public void A_request_already_answered_reports_its_outcome_as_operation_completed()
    {
        using var ui = new UiThread();
        var ledger = new RequestLedger();
        var events = new EventHub(new MemoryLog());
        var listener = new CapturingReply();
        events.Attach(listener);
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var registry = TestOps.Registry(echo: (r, c) => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return new EchoResult { Text = r.Text }; });
        var dispatcher = ui.Invoke(() => new Dispatcher(new RequestExecutor(registry, new MemoryLog()), new MemoryLog(), null, ledger, events));
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "slow"), DateTime.UtcNow, reply);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(ledger.Running!.TryAnswer("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}", "timeout"));
        release.Set();

        Assert.True(listener.Lines.TryTake(out var line, 10000));
        var p = (JsonObject)((JsonObject)Json.Parse(line!)!)["params"]!;
        Assert.Equal("operation.completed", p["kind"]);
        var data = (JsonObject)p["data"]!;
        Assert.Equal(1L, data["id"]);
        Assert.Equal(true, data["ok"]);
        Assert.Equal("timeout", data["answered_as"]);
        Assert.Single(reply.Lines);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void Posted_actions_run_on_the_UI_thread_before_the_next_request()
    {
        using var ui = new UiThread();
        var order = new ConcurrentQueue<string>();
        var registry = TestOps.Registry(echo: (r, c) => { order.Enqueue("request"); return new EchoResult { Text = r.Text }; });
        var dispatcher = Create(ui, registry, new MemoryLog());
        int actionThread = 0;
        dispatcher.Post("restore", () => { actionThread = Thread.CurrentThread.ManagedThreadId; order.Enqueue("action"); });
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        reply.Next();
        Assert.Equal(new[] { "action", "request" }, order.ToArray());
        Assert.Equal(ui.ThreadId, actionThread);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void After_each_request_the_hook_runs_on_the_UI_thread()
    {
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        var ran = new ManualResetEventSlim(false);
        int hookThread = 0;
        dispatcher.AfterEachRequest = () => { hookThread = Thread.CurrentThread.ManagedThreadId; ran.Set(); };
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        reply.Next();
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(ui.ThreadId, hookThread);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void The_hook_runs_before_the_answer_is_sent()
    {
        // Issue #11: addin.state answers from the table the hook refreshes, so it must be fresh by the time the client has the answer.
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        var reply = new CapturingReply();
        int answersWhenHooked = -1;
        var hooked = new ManualResetEventSlim(false);
        dispatcher.AfterEachRequest = () =>
        {
            answersWhenHooked = reply.Lines.Count;
            hooked.Set();
        };
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        Assert.True(hooked.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, answersWhenHooked);
        reply.Next();
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void The_hook_still_runs_once_for_a_request_its_client_dropped()
    {
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        int hooks = 0;
        var ran = new ManualResetEventSlim(false);
        dispatcher.AfterEachRequest = () => { hooks++; ran.Set(); };
        var reply = new CapturingReply { Open = false };
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)));
        ui.Invoke(dispatcher.Dispose);
        Assert.Equal(1, hooks);
    }

    [Fact]
    public void A_probe_records_that_the_UI_thread_pumped()
    {
        using var ui = new UiThread();
        var dispatcher = Create(ui, TestOps.Registry(), new MemoryLog());
        var before = DateTime.UtcNow;
        dispatcher.Probe();
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((dispatcher.LastPumpedUtc ?? DateTime.MinValue) < before && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.True(dispatcher.LastPumpedUtc >= before);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void No_request_starts_while_a_dialog_is_open_and_posted_actions_wait_for_it_to_close()
    {
        using var ui = new UiThread();
        int runs = 0;
        var registry = TestOps.Registry(echo: (r, c) => { Interlocked.Increment(ref runs); return new EchoResult { Text = r.Text }; });
        var dispatcher = Create(ui, registry, new MemoryLog());
        DialogInfo? open = new DialogInfo { Id = "d1-10", Title = "System Options", Buttons = { new DialogButton { Text = "Cancel", ControlId = 2 } } };
        dispatcher.OpenDialog = () => Volatile.Read(ref open);
        var restored = new ManualResetEventSlim(false);
        dispatcher.Post("restore", () => restored.Set());

        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "a"), DateTime.UtcNow, reply);
        var refused = reply.Next();
        Assert.Equal(ErrorCode.DialogOpen, refused.Error!.Code);
        Assert.True(refused.Error.Details!["started"] is false);
        Assert.Contains("d1-10", refused.Error.Message);
        Assert.Equal(0, runs);
        Assert.False(restored.Wait(200));

        Volatile.Write(ref open, null);
        Assert.True(restored.Wait(TimeSpan.FromSeconds(10)));
        dispatcher.Enqueue(Requests.Echo(2, "b"), DateTime.UtcNow, reply);
        Assert.True(reply.Next().IsSuccess);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void A_request_sent_right_after_a_respond_starts()
    {
        using var ui = new UiThread();
        var ledger = new RequestLedger();
        var hub = new EventHub(new MemoryLog());
        var windows = new FakeWindows();
        var watch = new DialogWatch(windows, ledger, hub, new MemoryLog());
        var dispatcher = ui.Invoke(() => new Dispatcher(new RequestExecutor(TestOps.Registry(), new MemoryLog()), new MemoryLog(), null, ledger, hub));
        dispatcher.OpenDialog = () => watch.Current;
        windows.Show(Dialogs.Box(77, "Options", "OK", "Cancel"));
        watch.Tick();
        var reply = new CapturingReply();
        dispatcher.Enqueue(Requests.Echo(1, "under the dialog"), DateTime.UtcNow, reply);
        Assert.Equal(ErrorCode.DialogOpen, reply.Next().Error!.Code);

        watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "Cancel" }, DateTime.UtcNow.AddSeconds(10));
        dispatcher.Enqueue(Requests.Echo(2, "right after"), DateTime.UtcNow, reply);
        var after = reply.Next();
        Assert.Equal(2L, after.Id);
        Assert.True(after.IsSuccess);
        ui.Invoke(dispatcher.Dispose);
    }

    [Fact]
    public void A_finished_request_keeps_its_outcome()
    {
        using var ui = new UiThread();
        var ledger = new RequestLedger();
        var dispatcher = ui.Invoke(() => new Dispatcher(new RequestExecutor(TestOps.Registry(), new MemoryLog()), new MemoryLog(), null, ledger));
        var reply = new CapturingReply();
        var entry = ledger.Enqueue(Requests.Echo(1, "kept"), DateTime.UtcNow, reply);
        dispatcher.Enqueue(Requests.Echo(2, "x"), DateTime.UtcNow, reply); // schedules the run; entry 1 is first in the ledger
        reply.Next();
        reply.Next();
        Assert.True(entry.Outcome!.IsSuccess);
        ui.Invoke(dispatcher.Dispose);
    }
}
