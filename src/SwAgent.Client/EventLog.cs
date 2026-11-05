using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.Client;

/// <summary>
/// The events the server has received, for sw_events: the last <see cref="Capacity"/> from the current SOLIDWORKS
/// process, oldest first. The server numbers them itself, so a cursor stays valid across an add-in reload or a new
/// SOLIDWORKS, whose own sequence starts again at 1. A skip in the add-in's sequence is kept as a gap, and reading
/// across a gap (or across events already dropped) says <c>missed</c>.
/// </summary>
public sealed class EventLog
{
    public const int Capacity = 1000;
    public const int DefaultLimit = 100;

    readonly object _gate = new object();
    readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
    long _lastSeq;
    long _droppedThrough;
    long _lastAddinSeq;
    int? _processId;

    /// <summary>The process the kept events came from; null before any.</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate) return _processId;
        }
    }

    /// <summary>The add-in's own sequence of the last event kept, for catching up after a reconnect to the same process.</summary>
    public long LastAddinSeq
    {
        get
        {
            lock (_gate) return _lastAddinSeq;
        }
    }

    /// <summary>The server sequence of the last event kept.</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate) return _lastSeq;
        }
    }

    /// <summary>
    /// Keeps one event from SOLIDWORKS process <paramref name="processId"/>. Events from another process replace the
    /// kept ones. An event whose add-in sequence was already kept (the replay on connect overlapping live events) is
    /// dropped as a duplicate.
    /// </summary>
    public void Add(int processId, EventEnvelope envelope)
    {
        lock (_gate)
        {
            if (_processId != processId)
            {
                _droppedThrough = _lastSeq;
                _entries.Clear();
                _lastAddinSeq = 0;
                _processId = processId;
            }
            bool gap;
            if (envelope.Seq > _lastAddinSeq)
            {
                gap = envelope.Seq != _lastAddinSeq + 1;
            }
            else
            {
                if (_entries.Any(e => e.AddinSeq == envelope.Seq && e.Envelope.Kind == envelope.Kind && e.Envelope.Time == envelope.Time)) return;
                gap = envelope.Seq != 1; // the add-in was loaded again and counts from 1
            }
            _lastAddinSeq = envelope.Seq;
            long seq = ++_lastSeq;
            _entries.AddLast(new Entry(seq, envelope.Seq, gap,
                new EventEnvelope { Seq = seq, Kind = envelope.Kind, Time = envelope.Time, Data = envelope.Data }));
            while (_entries.Count > Capacity)
            {
                _droppedThrough = _entries.First!.Value.Seq;
                _entries.RemoveFirst();
            }
        }
    }

    /// <summary>The kept events after <paramref name="afterSeq"/>, of <paramref name="kinds"/> when given, at most <paramref name="limit"/>.</summary>
    public SwEventsResult Read(long? afterSeq, IReadOnlyCollection<string>? kinds, int? limit)
    {
        int max = limit ?? DefaultLimit;
        if (max < 1 || max > Capacity)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"limit must be between 1 and {Capacity}, not {max}",
                new Contracts.Serialization.JsonObject { ["field"] = "limit" });
        }
        long after = afterSeq ?? 0;
        lock (_gate)
        {
            var result = new SwEventsResult { ProcessId = _processId, Missed = after < _droppedThrough, LastSeq = Math.Max(after, _lastSeq) };
            foreach (var entry in _entries.Where(e => e.Seq > after))
            {
                bool matches = kinds == null || kinds.Count == 0 || kinds.Contains(entry.Envelope.Kind);
                if (matches && result.Events.Count == max)
                {
                    result.More = true;
                    result.LastSeq = result.Events[result.Events.Count - 1].Seq;
                    break;
                }
                if (entry.GapBefore) result.Missed = true;
                if (matches) result.Events.Add(entry.Envelope);
            }
            return result;
        }
    }

    sealed class Entry
    {
        public Entry(long seq, long addinSeq, bool gapBefore, EventEnvelope envelope)
        {
            Seq = seq;
            AddinSeq = addinSeq;
            GapBefore = gapBefore;
            Envelope = envelope;
        }

        public long Seq { get; }
        public long AddinSeq { get; }
        public bool GapBefore { get; }
        public EventEnvelope Envelope { get; }
    }
}
