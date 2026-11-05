using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SwAgent.Contracts.Rpc;

namespace SwAgent.AddIn.Core;

public enum LedgerState
{
    Queued,
    Running,
    Finished,
}

/// <summary>One accepted request. It is answered exactly once, by whichever caller of <see cref="TryAnswer"/> comes first.</summary>
public sealed class LedgerEntry
{
    int _answered;
    long _startedTicks;
    long _finishedTicks;
    volatile string? _answeredAs;
    RpcResponse? _outcome;

    internal LedgerEntry(RpcRequest request, DateTime receivedUtc, IReplyChannel reply)
    {
        Request = request;
        ReceivedUtc = receivedUtc;
        DeadlineUtc = receivedUtc.AddMilliseconds(request.DeadlineMs);
        Reply = reply;
    }

    public RpcRequest Request { get; }
    public DateTime ReceivedUtc { get; }
    public DateTime DeadlineUtc { get; }
    public IReplyChannel Reply { get; }

    /// <summary>Changed only under the ledger's lock.</summary>
    public LedgerState State { get; internal set; }

    /// <summary>When it started on the UI thread; null if it never did.</summary>
    public DateTime? StartedUtc => Ticks(ref _startedTicks);

    /// <summary>When it finished running; null while running or if it never ran.</summary>
    public DateTime? FinishedUtc => Ticks(ref _finishedTicks);

    public bool IsAnswered => Volatile.Read(ref _answered) != 0;

    /// <summary>The response its own run produced, set before it is finished; null while running or if it never ran.</summary>
    public RpcResponse? Outcome
    {
        get => Volatile.Read(ref _outcome);
        internal set => Volatile.Write(ref _outcome, value);
    }

    /// <summary>What the client was told first: result, busy, timeout, dialog_open, unloading.</summary>
    public string? AnsweredAs => _answeredAs;

    /// <summary>Sends <paramref name="line"/> if nothing answered this request yet; false if something already had.</summary>
    public bool TryAnswer(string line, string answeredAs)
    {
        if (Interlocked.CompareExchange(ref _answered, 1, 0) != 0) return false;
        _answeredAs = answeredAs;
        Reply.Send(line);
        return true;
    }

    internal void MarkStarted(DateTime utc) => Interlocked.Exchange(ref _startedTicks, utc.Ticks);

    internal void MarkFinished(DateTime utc) => Interlocked.Exchange(ref _finishedTicks, utc.Ticks);

    static DateTime? Ticks(ref long field)
    {
        long ticks = Interlocked.Read(ref field);
        return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
    }
}

public sealed class LedgerSnapshot
{
    public LedgerSnapshot(LedgerEntry? running, IReadOnlyList<LedgerEntry> queued)
    {
        Running = running;
        Queued = queued;
    }

    public LedgerEntry? Running { get; }
    public IReadOnlyList<LedgerEntry> Queued { get; }
}

/// <summary>
/// Every request the add-in accepted for the UI thread: queued in arrival order, at most one running, then finished.
/// An entry whose deadline has passed is never started. Thread safe: the dispatcher starts and finishes entries,
/// the watchdog expires them, and pipe operations read snapshots.
/// </summary>
public sealed class RequestLedger
{
    static readonly IReadOnlyList<(LedgerEntry Entry, int QueuedAhead)> None = Array.Empty<(LedgerEntry, int)>();

    readonly object _gate = new object();
    readonly LinkedList<LedgerEntry> _queue = new LinkedList<LedgerEntry>();
    LedgerEntry? _running;
    LedgerEntry? _lastFinished;

    public LedgerEntry Enqueue(RpcRequest request, DateTime receivedUtc, IReplyChannel reply)
    {
        var entry = new LedgerEntry(request, receivedUtc, reply);
        lock (_gate) _queue.AddLast(entry);
        return entry;
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public LedgerEntry? Running
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>The entry that ran most recently, for a busy answer given after it finished.</summary>
    public LedgerEntry? LastFinished
    {
        get { lock (_gate) return _lastFinished; }
    }

    /// <summary>
    /// Starts the oldest queued entry whose deadline has not passed. Entries ahead of it whose deadline has passed are
    /// removed and returned in <paramref name="skipped"/>, never started, for the caller to answer busy. False when
    /// nothing was started: the queue is empty (after skipping), or an entry is already running.
    /// </summary>
    public bool TryStart(DateTime nowUtc, out LedgerEntry? entry, out IReadOnlyList<(LedgerEntry Entry, int QueuedAhead)> skipped)
    {
        lock (_gate)
        {
            entry = null;
            skipped = None;
            if (_running != null) return false;
            List<(LedgerEntry, int)>? expired = null;
            int position = 0;
            while (_queue.First != null)
            {
                var first = _queue.First.Value;
                _queue.RemoveFirst();
                if (nowUtc >= first.DeadlineUtc)
                {
                    first.State = LedgerState.Finished;
                    (expired ??= new List<(LedgerEntry, int)>()).Add((first, position++));
                    continue;
                }
                first.State = LedgerState.Running;
                first.MarkStarted(nowUtc);
                _running = first;
                entry = first;
                break;
            }
            if (expired != null) skipped = expired;
            return entry != null;
        }
    }

    public void Finish(LedgerEntry entry, DateTime? finishedUtc = null)
    {
        lock (_gate)
        {
            entry.State = LedgerState.Finished;
            if (finishedUtc != null) entry.MarkFinished(finishedUtc.Value);
            if (ReferenceEquals(_running, entry))
            {
                _running = null;
                _lastFinished = entry;
            }
        }
    }

    /// <summary>Removes the queued entries whose deadline has passed, each with how many entries were queued ahead of it.</summary>
    public IReadOnlyList<(LedgerEntry Entry, int QueuedAhead)> TakeExpired(DateTime nowUtc)
    {
        var expired = new List<(LedgerEntry, int)>();
        lock (_gate)
        {
            int position = 0;
            var node = _queue.First;
            while (node != null)
            {
                var next = node.Next;
                if (nowUtc >= node.Value.DeadlineUtc)
                {
                    node.Value.State = LedgerState.Finished;
                    expired.Add((node.Value, position));
                    _queue.Remove(node);
                }
                position++;
                node = next;
            }
        }
        return expired;
    }

    /// <summary>Empties the queue (the add-in is unloading); the running entry is left to finish.</summary>
    public IReadOnlyList<LedgerEntry> TakeAllQueued()
    {
        lock (_gate)
        {
            var all = _queue.ToList();
            _queue.Clear();
            foreach (var e in all) e.State = LedgerState.Finished;
            return all;
        }
    }

    public LedgerSnapshot Snapshot()
    {
        lock (_gate) return new LedgerSnapshot(_running, _queue.ToList());
    }
}
