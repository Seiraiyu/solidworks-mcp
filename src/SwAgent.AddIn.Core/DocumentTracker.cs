using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The add-in's view of the open documents and their dirty flags, kept so a crash can name what was unsaved.
/// Refreshed on the UI thread after every request and every SOLIDWORKS document event; read from any thread.
/// </summary>
public sealed class DocumentTracker
{
    readonly Func<IReadOnlyList<DocumentInfo>> _read;
    readonly EventHub _events;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    volatile IReadOnlyList<DocumentInfo> _current = Array.Empty<DocumentInfo>();
    long _asOfTicks;

    /// <param name="read">Reads the open documents; called on the UI thread only.</param>
    public DocumentTracker(Func<IReadOnlyList<DocumentInfo>> read, EventHub events, ILog log, Func<DateTime>? utcNow = null)
    {
        _read = read;
        _events = events;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<DocumentInfo> Current => _current;

    public DateTime? AsOfUtc
    {
        get
        {
            long ticks = System.Threading.Interlocked.Read(ref _asOfTicks);
            return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>UI thread. Re-reads the open documents; raises documents.changed when the table differs.</summary>
    public void Refresh()
    {
        IReadOnlyList<DocumentInfo> now;
        try
        {
            now = _read().ToList();
        }
        catch (Exception e)
        {
            _log.Warn($"documents: could not read the open documents ({e.Message}); keeping the last table");
            return;
        }
        System.Threading.Interlocked.Exchange(ref _asOfTicks, _utcNow().Ticks);
        if (Same(_current, now)) return;
        _current = now;
        _events.Publish("documents.changed", new DocumentsChangedEvent { Documents = now.ToList() });
    }

    public void Opened(DocumentEvent e) => Raise("document.opened", e);

    public void Closed(DocumentEvent e) => Raise("document.closed", e);

    public void Saved(DocumentEvent e) => Raise("document.saved", e);

    public void Activated(DocumentEvent e) => Raise("document.activated", e);

    public void Rebuilt(DocumentRebuiltEvent e) => Raise("document.rebuilt", e);

    void Raise(string kind, object data)
    {
        _events.Publish(kind, data);
        Refresh();
    }

    static bool Same(IReadOnlyList<DocumentInfo> a, IReadOnlyList<DocumentInfo> b) =>
        a.Count == b.Count && a.Zip(b, (x, y) => x.Title == y.Title && x.Path == y.Path && x.Type == y.Type && x.Dirty == y.Dirty).All(s => s);
}
