using System;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// A background thread (never the UI thread) that keeps the deadline promise: a queued request past its deadline
/// is removed and answered busy (never started, safe to retry); a running request past its deadline is answered
/// timeout and left to finish, its outcome later raised as operation.completed. It also asks the UI thread to
/// prove it still pumps messages.
/// </summary>
public sealed class Watchdog : IDisposable
{
    readonly RequestLedger _ledger;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    readonly Action? _probe;
    readonly TimeSpan _period;
    readonly int _probeEvery;
    readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
    Thread? _thread;

    public Watchdog(RequestLedger ledger, ILog log, Func<DateTime>? utcNow = null, Action? probe = null, TimeSpan? period = null, int probeEvery = 10)
    {
        _ledger = ledger;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probe = probe;
        _period = period ?? TimeSpan.FromMilliseconds(100);
        _probeEvery = Math.Max(1, probeEvery);
    }

    public bool IsAlive => _thread?.IsAlive == true;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "swagent-watchdog" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>One pass over the ledger.</summary>
    public void Tick()
    {
        var now = _utcNow();
        var running = _ledger.Running;
        foreach (var (entry, ahead) in _ledger.TakeExpired(now))
        {
            var error = DeadlineErrors.Busy(entry, running, running == null ? _ledger.LastFinished : null, ahead, now);
            if (entry.TryAnswer(RpcCodec.Failure(entry.Request.Id, RpcErrorCodes.ServerError, error), "busy"))
                _log.Info($"request {entry.Request.Id} {entry.Request.Method}: busy, not started after {Ms(now - entry.ReceivedUtc)} ms");
        }
        // Timeout only for a request that started in time; one that did not is the executor's busy, never a timeout.
        if (running != null && !running.IsAnswered && now >= running.DeadlineUtc
            && running.StartedUtc is DateTime startedAt && startedAt < running.DeadlineUtc)
        {
            var error = DeadlineErrors.Timeout(running, now);
            if (running.TryAnswer(RpcCodec.Failure(running.Request.Id, RpcErrorCodes.ServerError, error), "timeout"))
                _log.Warn($"request {running.Request.Id} {running.Request.Method}: timeout after running {Ms(now - (running.StartedUtc ?? now))} ms; left to finish");
        }
    }

    void Loop()
    {
        int ticks = 0;
        while (!_stop.Wait(_period))
        {
            try
            {
                Tick();
                if (_probe != null && ++ticks % _probeEvery == 0) _probe();
            }
            catch (Exception e)
            {
                // Never take SOLIDWORKS down from here; log and keep watching.
                _log.Error("watchdog: a pass failed", e);
            }
        }
    }

    static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);
}
