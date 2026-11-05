using System;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class EventHubTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    static EventHub Hub(int capacity = 256) => new EventHub(new MemoryLog(), () => T0, capacity);

    [Fact]
    public void Publishes_a_numbered_notification_to_the_attached_client()
    {
        var hub = Hub();
        var client = new CapturingReply();
        hub.Attach(client);
        hub.Publish("session.closing", new SessionClosingEvent { Reason = "bye" });
        Assert.True(client.Lines.TryTake(out var line, 1000));
        var o = (JsonObject)Json.Parse(line!)!;
        Assert.Equal("event", o["method"]);
        Assert.False(o.ContainsKey("id"));
        var p = (JsonObject)o["params"]!;
        Assert.Equal(1L, p["seq"]);
        Assert.Equal("session.closing", p["kind"]);
        Assert.Equal("2026-11-05T12:00:00.0000000Z", p["time"]);
        Assert.Equal("bye", ((JsonObject)p["data"]!)["reason"]);
        Assert.Equal(1L, hub.LastSeq);
    }

    [Fact]
    public void Without_a_client_events_are_only_held()
    {
        var hub = Hub();
        hub.Publish("session.closing", new SessionClosingEvent { Reason = "a" });
        var held = hub.Since(0);
        Assert.Single(held.Events);
        Assert.False(held.Missed);
    }

    [Fact]
    public void Detach_only_detaches_the_client_it_names()
    {
        var hub = Hub();
        var first = new CapturingReply();
        var second = new CapturingReply();
        hub.Attach(first);
        hub.Attach(second);
        hub.Detach(first);
        hub.Publish("session.closing", new SessionClosingEvent { Reason = "x" });
        Assert.Empty(first.Lines);
        Assert.Single(second.Lines);
    }

    [Fact]
    public void Since_returns_events_after_a_sequence_and_says_when_some_were_dropped()
    {
        var hub = Hub(capacity: 3);
        for (int i = 1; i <= 5; i++) hub.Publish("session.closing", new SessionClosingEvent { Reason = i.ToString() });
        var all = hub.Since(null);
        Assert.Equal(new[] { 3L, 4L, 5L }, all.Events.Select(e => e.Seq).ToArray());
        Assert.True(hub.Since(1).Missed);
        Assert.False(hub.Since(2).Missed);
        Assert.Equal(new[] { 5L }, hub.Since(4).Events.Select(e => e.Seq).ToArray());
        Assert.Empty(hub.Since(5).Events);
    }

    [Fact]
    public void A_kind_must_exist_and_match_its_data_type()
    {
        var hub = Hub();
        Assert.Throws<ArgumentException>(() => hub.Publish("nope.nope", new SessionClosingEvent()));
        Assert.Throws<ArgumentException>(() => hub.Publish("document.saved", new SessionClosingEvent()));
    }
}
