using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

[Event("test.happened")] public sealed class HappenedEvent { }
[Event("test.happened")] public sealed class HappenedAgainEvent { }

public class EventCatalogTests
{
    [Fact]
    public void Catalog_holds_the_phase_2B_kinds()
    {
        Assert.Equal(
            new[]
            {
                "dialog.answered", "dialog.closed", "dialog.opened",
                "document.activated", "document.closed", "document.opened", "document.rebuilt", "document.saved",
                "documents.changed", "operation.completed", "session.closing",
            },
            EventCatalog.All.Select(e => e.Kind).ToArray());
        Assert.Equal(typeof(DocumentEvent), EventCatalog.Find("document.saved")!.DataType);
        Assert.Null(EventCatalog.Find("nope.nope"));
    }

    [Fact]
    public void Two_types_cannot_claim_one_kind()
    {
        Assert.Throws<ArgumentException>(() => EventCatalog.FromTypes(new[] { typeof(HappenedEvent), typeof(HappenedAgainEvent) }));
    }

    public static IEnumerable<object[]> Kinds() => EventCatalog.All.Select(e => new object[] { e.Kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_event_type_round_trips_with_every_property_set(string kind)
    {
        var type = EventCatalog.Find(kind)!.DataType;
        object filled = Samples.Fill(type);
        string once = JsonCodec.Serialize(filled);
        Assert.Equal(once, JsonCodec.Serialize(JsonCodec.Deserialize(once, type)));
    }

    [Fact]
    public void A_notification_is_a_line_with_a_method_and_no_id()
    {
        var envelope = new EventEnvelope { Seq = 3, Kind = "session.closing", Time = "2026-11-05T12:00:00.0000000Z", Data = new JsonObject { ["reason"] = "r" } };
        string line = RpcCodec.FormatNotification(EventEnvelope.Method, JsonCodec.ToJson(envelope));
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"method\":\"event\",\"params\":{\"seq\":3,\"kind\":\"session.closing\",\"time\":\"2026-11-05T12:00:00.0000000Z\",\"data\":{\"reason\":\"r\"}}}", line);
    }
}
