using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Raises events: numbers them, holds the last <c>capacity</c> for a client that missed them, and sends each as an
/// <c>event</c> notification to the connected client. Thread safe; Publish never blocks on the pipe and never throws
/// for a valid kind.
/// </summary>
public sealed class EventHub
{
    public const int DefaultCapacity = 256;

    readonly object _gate = new object();
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    readonly int _capacity;
    readonly Queue<EventEnvelope> _held = new Queue<EventEnvelope>();
    long _seq;
    IReplyChannel? _client;

    public EventHub(ILog log, Func<DateTime>? utcNow = null, int capacity = DefaultCapacity)
    {
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _capacity = capacity;
    }

    public long LastSeq
    {
        get { lock (_gate) return _seq; }
    }

    public void Attach(IReplyChannel client)
    {
        lock (_gate) _client = client;
    }

    public void Detach(IReplyChannel client)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_client, client)) _client = null;
        }
    }

    /// <summary>Raises <paramref name="kind"/>, whose contract data type must be the type of <paramref name="data"/>.</summary>
    public EventEnvelope Publish(string kind, object data)
    {
        var descriptor = EventCatalog.Find(kind) ?? throw new ArgumentException($"no event kind '{kind}' is declared");
        if (descriptor.DataType != data.GetType())
            throw new ArgumentException($"event '{kind}' carries {descriptor.DataType.Name}, not {data.GetType().Name}");
        var json = (JsonObject)JsonCodec.ToJson(data)!;
        EventEnvelope envelope;
        IReplyChannel? client;
        lock (_gate)
        {
            envelope = new EventEnvelope
            {
                Seq = ++_seq,
                Kind = kind,
                Time = _utcNow().ToString("o", CultureInfo.InvariantCulture),
                Data = json,
            };
            _held.Enqueue(envelope);
            while (_held.Count > _capacity) _held.Dequeue();
            client = _client;
        }
        if (client != null && client.IsOpen) client.Send(RpcCodec.FormatNotification(EventEnvelope.Method, JsonCodec.ToJson(envelope)));
        if (_log.IsDebugEnabled) _log.Debug($"event {envelope.Seq} {kind}");
        return envelope;
    }

    /// <summary>The held events after <paramref name="afterSeq"/> (all when null), and whether some after it were already dropped.</summary>
    public (IReadOnlyList<EventEnvelope> Events, bool Missed) Since(long? afterSeq)
    {
        lock (_gate)
        {
            long after = afterSeq ?? 0;
            var events = _held.Where(e => e.Seq > after).ToList();
            long oldest = _held.Count == 0 ? _seq + 1 : _held.Peek().Seq;
            bool missed = afterSeq != null && after + 1 < oldest && after < _seq;
            return (events, missed);
        }
    }
}
