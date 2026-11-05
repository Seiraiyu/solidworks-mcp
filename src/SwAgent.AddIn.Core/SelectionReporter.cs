using System;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Raises selection.changed for the user's selections (design 371, Selection). SOLIDWORKS notifies on every item of a
/// box select, so a notification only records how to read the selection and restarts a timer: the selection is read
/// once, after it has been still for <see cref="Quiet"/>, back on the UI thread (through <c>postToUi</c>, as SOLIDWORKS
/// may only be read there), and raised only when it differs from the last one raised. Notifications while one of the
/// add-in's own operations runs are not the user's and are dropped, unless the operation stands in for the user
/// (diag.select). Nothing here throws: the timer callback and the read are wrapped, because an exception on a pool
/// thread would take SOLIDWORKS down.
/// </summary>
public sealed class SelectionReporter : IDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(250);

    readonly object _gate = new object();
    readonly EventHub _events;
    readonly Func<bool> _operationRunning;
    readonly ILog _log;
    readonly Action<Action> _postToUi;
    readonly Timer _timer;
    Func<SelectionChangedEvent>? _pending;
    string? _lastRaised;
    int _allowed;
    bool _disposed;

    /// <param name="postToUi">Runs an action on SOLIDWORKS' UI thread, between requests (the dispatcher's Post).</param>
    public SelectionReporter(EventHub events, Func<bool> operationRunning, ILog log, Action<Action> postToUi, TimeSpan? quiet = null)
    {
        _events = events;
        _operationRunning = operationRunning;
        _log = log;
        _postToUi = postToUi;
        Quiet = quiet ?? DefaultQuiet;
        _timer = new Timer(_ => OnQuiet(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan Quiet { get; }

    /// <summary>True while selections SOLIDWORKS reports are the add-in's own.</summary>
    public bool Suppressed => _operationRunning() && Volatile.Read(ref _allowed) == 0;

    /// <summary>For the life of the returned scope, selections made by the running operation are reported as the user's.</summary>
    public IDisposable AllowDuringOperation()
    {
        Interlocked.Increment(ref _allowed);
        return new Scope(this);
    }

    /// <summary>
    /// The selection changed; <paramref name="read"/> reads it on the UI thread. Cheap: nothing is read until the
    /// selection has been still for <see cref="Quiet"/>. Dropped while suppressed.
    /// </summary>
    public void Changed(Func<SelectionChangedEvent> read)
    {
        if (Suppressed) return;
        lock (_gate)
        {
            if (_disposed) return;
            _pending = read;
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads and raises the pending selection now (through the UI thread). The timer calls it; tests may too.</summary>
    public void Flush() => OnQuiet();

    void OnQuiet()
    {
        try
        {
            Func<SelectionChangedEvent>? read;
            lock (_gate)
            {
                read = _pending;
                _pending = null;
                if (read == null || _disposed) return;
            }
            _postToUi(() => Raise(read));
        }
        catch (Exception e)
        {
            _log.Error("selection: scheduling the selection read failed", e);
        }
    }

    void Raise(Func<SelectionChangedEvent> read)
    {
        try
        {
            var selection = read();
            string json = JsonCodec.Serialize(selection);
            lock (_gate)
            {
                if (_disposed || json == _lastRaised) return;
                _lastRaised = json;
            }
            _events.Publish("selection.changed", selection);
        }
        catch (Exception e)
        {
            _log.Error("selection: reading or raising the selection failed", e);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
        }
        _timer.Dispose();
    }

    sealed class Scope : IDisposable
    {
        SelectionReporter? _owner;

        public Scope(SelectionReporter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null) Interlocked.Decrement(ref owner._allowed);
        }
    }
}
