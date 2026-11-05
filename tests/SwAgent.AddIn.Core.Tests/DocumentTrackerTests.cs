using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DocumentTrackerTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    static DocumentInfo Doc(string title, bool dirty = false) => new DocumentInfo { Title = title, Path = @"C:\m\" + title + ".sldprt", Type = DocumentType.Part, Dirty = dirty };

    static List<string> Kinds(CapturingReply client) =>
        client.Lines.ToArray().Select(l => (string)((JsonObject)((JsonObject)Json.Parse(l)!)["params"]!)["kind"]!).ToList();

    [Fact]
    public void Refresh_publishes_documents_changed_only_when_the_table_changes()
    {
        var open = new List<DocumentInfo> { Doc("a") };
        var hub = new EventHub(new MemoryLog(), () => T0);
        var client = new CapturingReply();
        hub.Attach(client);
        var tracker = new DocumentTracker(() => open.ToList(), hub, new MemoryLog(), () => T0);

        tracker.Refresh();
        tracker.Refresh();
        open[0] = Doc("a", dirty: true);
        tracker.Refresh();

        Assert.Equal(new[] { "documents.changed", "documents.changed" }, Kinds(client));
        Assert.True(tracker.Current.Single().Dirty);
        Assert.Equal(T0, tracker.AsOfUtc);
    }

    [Fact]
    public void Lifecycle_events_are_published_then_the_table_refreshed()
    {
        var open = new List<DocumentInfo>();
        var hub = new EventHub(new MemoryLog(), () => T0);
        var client = new CapturingReply();
        hub.Attach(client);
        var tracker = new DocumentTracker(() => open.ToList(), hub, new MemoryLog(), () => T0);

        open.Add(Doc("a"));
        tracker.Opened(new DocumentEvent { Title = "a", Type = DocumentType.Part });
        tracker.Saved(new DocumentEvent { Title = "a" });
        tracker.Rebuilt(new DocumentRebuiltEvent { Title = "a", Errors = 1 });
        tracker.Activated(new DocumentEvent { Title = "a" });
        open.Clear();
        tracker.Closed(new DocumentEvent { Title = "a" });

        Assert.Equal(new[] { "document.opened", "documents.changed", "document.saved", "document.rebuilt", "document.activated", "document.closed", "documents.changed" }, Kinds(client));
    }

    [Fact]
    public void A_failing_read_is_logged_and_keeps_the_last_table()
    {
        bool fail = false;
        var log = new MemoryLog();
        var tracker = new DocumentTracker(() => fail ? throw new InvalidOperationException("gone") : new List<DocumentInfo> { Doc("a") },
            new EventHub(log), log, () => T0);
        tracker.Refresh();
        fail = true;
        tracker.Refresh();
        Assert.Single(tracker.Current);
        Assert.True(log.Contains("could not read the open documents"));
    }
}
