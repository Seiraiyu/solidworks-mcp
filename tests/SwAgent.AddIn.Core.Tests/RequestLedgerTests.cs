using System;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class RequestLedgerTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Starts_requests_in_order_one_at_a_time()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "a"), T0, reply);
        ledger.Enqueue(Requests.Echo(2, "b"), T0, reply);
        Assert.True(ledger.TryStart(T0, out var first, out var skipped));
        Assert.Empty(skipped);
        Assert.Equal(1L, first!.Request.Id);
        Assert.Equal(LedgerState.Running, first.State);
        Assert.Equal(T0, first.StartedUtc);
        Assert.False(ledger.TryStart(T0, out _, out _));
        ledger.Finish(first);
        Assert.Equal(LedgerState.Finished, first.State);
        Assert.Same(first, ledger.LastFinished);
        Assert.True(ledger.TryStart(T0, out var second, out _));
        Assert.Equal(2L, second!.Request.Id);
    }

    [Fact]
    public void Try_start_never_starts_an_expired_entry_and_hands_it_back()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "late", deadlineMs: 100), T0, reply);
        ledger.Enqueue(Requests.Echo(2, "later", deadlineMs: 100), T0, reply);
        ledger.Enqueue(Requests.Echo(3, "fine", deadlineMs: 10_000), T0, reply);
        Assert.True(ledger.TryStart(T0.AddMilliseconds(100), out var started, out var skipped));
        Assert.Equal(3L, started!.Request.Id);
        Assert.Equal(new[] { (1L, 0), (2L, 1) }, skipped.Select(s => (s.Entry.Request.Id, s.QueuedAhead)).ToArray());
        Assert.All(skipped, s => Assert.Equal(LedgerState.Finished, s.Entry.State));
        Assert.All(skipped, s => Assert.Null(s.Entry.StartedUtc));
    }

    [Fact]
    public void Try_start_with_only_expired_entries_starts_nothing()
    {
        var ledger = new RequestLedger();
        ledger.Enqueue(Requests.Echo(1, "late", deadlineMs: 100), T0, new CapturingReply());
        Assert.False(ledger.TryStart(T0.AddSeconds(1), out var started, out var skipped));
        Assert.Null(started);
        Assert.Single(skipped);
        Assert.Null(ledger.Running);
    }

    [Fact]
    public void An_entry_is_answered_exactly_once_even_under_a_race()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        var entry = ledger.Enqueue(Requests.Echo(1, "a"), T0, reply);
        var wins = Enumerable.Range(0, 32).AsParallel().Select(i => entry.TryAnswer("line " + i, "by " + i)).Count(w => w);
        Assert.Equal(1, wins);
        Assert.Single(reply.Lines);
        Assert.True(entry.IsAnswered);
        Assert.StartsWith("by ", entry.AnsweredAs);
    }

    [Fact]
    public void Take_expired_removes_only_queued_entries_past_their_deadline_with_how_many_were_ahead()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "a", deadlineMs: 10_000), T0, reply);
        ledger.Enqueue(Requests.Echo(2, "b", deadlineMs: 100), T0, reply);
        ledger.Enqueue(Requests.Echo(3, "c", deadlineMs: 10_000), T0, reply);
        ledger.Enqueue(Requests.Echo(4, "d", deadlineMs: 100), T0, reply);
        var expired = ledger.TakeExpired(T0.AddMilliseconds(100));
        Assert.Equal(new[] { (2L, 1), (4L, 3) }, expired.Select(x => (x.Entry.Request.Id, x.QueuedAhead)).ToArray());
        Assert.All(expired, x => Assert.Equal(LedgerState.Finished, x.Entry.State));
        Assert.Equal(new[] { 1L, 3L }, ledger.Snapshot().Queued.Select(e => e.Request.Id).ToArray());
    }

    [Fact]
    public void Snapshot_shows_the_running_entry_and_the_queue()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "a"), T0, reply);
        ledger.Enqueue(Requests.Echo(2, "b"), T0, reply);
        ledger.TryStart(T0, out _, out _);
        var snapshot = ledger.Snapshot();
        Assert.Equal(1L, snapshot.Running!.Request.Id);
        Assert.Equal(2L, snapshot.Queued.Single().Request.Id);
        Assert.Equal(1, ledger.QueuedCount);
    }

    [Fact]
    public void Take_all_queued_empties_the_queue_but_leaves_the_running_entry()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "a"), T0, reply);
        ledger.Enqueue(Requests.Echo(2, "b"), T0, reply);
        ledger.TryStart(T0, out _, out _);
        Assert.Equal(new[] { 2L }, ledger.TakeAllQueued().Select(e => e.Request.Id).ToArray());
        Assert.Equal(0, ledger.QueuedCount);
        Assert.NotNull(ledger.Running);
    }

    [Fact]
    public void Busy_names_what_runs_or_what_ran_before_and_how_many_were_ahead()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        var before = ledger.Enqueue(Requests.Echo(1, "first"), T0, reply);
        ledger.TryStart(T0, out _, out _);
        ledger.Finish(before);
        var late = ledger.Enqueue(Requests.Other(2, deadlineMs: 100), T0, reply);

        var error = DeadlineErrors.Busy(late, null, before, 2, T0.AddMilliseconds(400));

        Assert.Equal(ErrorCode.Busy, error.Code);
        var d = error.Details!;
        Assert.True(d["started"] is false);
        Assert.Equal(400L, d["waited_ms"]);
        Assert.Equal(2L, d["queued_ahead"]);
        var running = (JsonObject)d["running"]!;
        Assert.Equal(1L, running["id"]);
        Assert.Equal("test.echo", running["method"]);
        Assert.Contains("safe to retry", error.Message);
    }

    [Fact]
    public void Dialog_open_names_the_dialog_and_whether_the_request_started()
    {
        var ledger = new RequestLedger();
        var entry = ledger.Enqueue(Requests.Echo(1, "a"), T0, new CapturingReply());
        var dialog = new DialogInfo { Id = "d3-1f", Title = "Rebuild", Buttons = { new DialogButton { Text = "OK", ControlId = 1 } } };

        var waiting = DeadlineErrors.DialogOpen(entry, dialog, started: true);
        Assert.Equal(ErrorCode.DialogOpen, waiting.Code);
        Assert.True(waiting.Details!["operation_running"] is true);
        Assert.Equal("d3-1f", ((JsonObject)waiting.Details["dialog"]!)["id"]);
        Assert.Contains("operation.completed", waiting.Message);

        var refused = DeadlineErrors.DialogOpen(entry, dialog, started: false);
        Assert.True(refused.Details!["started"] is false);
        Assert.Contains("was not started", refused.Message);
        Assert.Contains("buttons: OK", refused.Message);
    }

    [Fact]
    public void An_entry_holds_the_outcome_of_its_own_run()
    {
        var entry = new RequestLedger().Enqueue(Requests.Echo(1, "a"), T0, new CapturingReply());
        Assert.Null(entry.Outcome);
        entry.Outcome = SwAgent.Contracts.Rpc.RpcResponse.Success(1, null);
        Assert.True(entry.Outcome.IsSuccess);
    }
}
