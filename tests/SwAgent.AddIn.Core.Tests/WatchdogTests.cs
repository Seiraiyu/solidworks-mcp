using System;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class WatchdogTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_queued_request_past_its_deadline_is_answered_busy_naming_what_runs()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "slow", deadlineMs: 60_000), T0, reply);
        ledger.TryStart(T0, out _, out _);
        ledger.Enqueue(Requests.Other(2, deadlineMs: 500), T0, reply);
        var now = T0;
        var dog = new Watchdog(ledger, new MemoryLog(), () => now);

        now = T0.AddMilliseconds(499);
        dog.Tick();
        Assert.Empty(reply.Lines);

        now = T0.AddMilliseconds(700);
        dog.Tick();
        var busy = reply.Next();
        Assert.Equal(2L, busy.Id);
        Assert.Equal(ErrorCode.Busy, busy.Error!.Code);
        var d = busy.Error.Details!;
        Assert.True(d["started"] is false);
        Assert.Equal(700L, d["waited_ms"]);
        Assert.Equal(0L, d["queued_ahead"]);
        var running = (JsonObject)d["running"]!;
        Assert.Equal(1L, running["id"]);
        Assert.Equal("test.echo", running["method"]);
        Assert.Equal(700L, running["running_ms"]);
        Assert.Contains("safe to retry", busy.Error.Message);
        Assert.Equal(0, ledger.QueuedCount);
    }

    [Fact]
    public void A_running_request_past_its_deadline_is_answered_timeout_once()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "slow", deadlineMs: 1000), T0, reply);
        ledger.TryStart(T0.AddMilliseconds(100), out var entry, out _);
        var now = T0.AddMilliseconds(1500);
        var dog = new Watchdog(ledger, new MemoryLog(), () => now);
        dog.Tick();
        dog.Tick();
        var timeout = reply.Next();
        Assert.Equal(ErrorCode.Timeout, timeout.Error!.Code);
        Assert.True(timeout.Error.Details!["started"] is true);
        Assert.Equal("test.echo", timeout.Error.Details["method"]);
        Assert.Equal(1400L, timeout.Error.Details["running_ms"]);
        Assert.Contains("operation.completed", timeout.Error.Message);
        Assert.Empty(reply.Lines);
        Assert.Equal("timeout", entry!.AnsweredAs);
    }

    [Fact]
    public void Timeout_is_only_for_a_request_that_started_before_its_deadline()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Echo(1, "x", deadlineMs: 100), T0, reply);
        ledger.TryStart(T0, out var entry, out _);
        entry!.MarkStarted(T0.AddMilliseconds(150)); // as if it had been started late: the ledger refuses to, but the watchdog must not rely on it
        var dog = new Watchdog(ledger, new MemoryLog(), () => T0.AddSeconds(1));
        dog.Tick();
        Assert.Empty(reply.Lines);
        Assert.False(entry.IsAnswered);
    }

    [Fact]
    public void Busy_with_nothing_running_says_the_UI_thread_did_not_take_it()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Other(1, deadlineMs: 100), T0, reply);
        var dog = new Watchdog(ledger, new MemoryLog(), () => T0.AddSeconds(1));
        dog.Tick();
        var busy = reply.Next();
        Assert.Equal(ErrorCode.Busy, busy.Error!.Code);
        Assert.Null(busy.Error.Details!["running"]);
        Assert.Contains("UI thread", busy.Error.Message);
    }

    [Fact]
    public void The_thread_ticks_and_probes_until_disposed()
    {
        var ledger = new RequestLedger();
        var reply = new CapturingReply();
        ledger.Enqueue(Requests.Other(1, deadlineMs: 1), DateTime.UtcNow, reply);
        int probes = 0;
        using (var dog = new Watchdog(ledger, new MemoryLog(), null, () => Interlocked.Increment(ref probes), TimeSpan.FromMilliseconds(20), probeEvery: 2))
        {
            dog.Start();
            Assert.True(dog.IsAlive);
            Assert.Equal(ErrorCode.Busy, reply.Next().Error!.Code);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref probes) == 0 && DateTime.UtcNow < until) Thread.Sleep(10);
            Assert.True(probes > 0);
        }
    }
}
