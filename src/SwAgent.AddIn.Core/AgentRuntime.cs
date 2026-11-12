using System;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The add-in's shared state: the ledger, the event hub, the document table and the preference guard. Pipe-thread
/// operations read it; the host wires it together. Create it before the operation registry.
/// </summary>
public sealed class AgentRuntime
{
    public AgentRuntime(string addinVersion, int processId, ILog log, Func<DateTime>? utcNow = null, bool diagnosticsEnabled = false)
    {
        AddinVersion = addinVersion;
        ProcessId = processId;
        Log = log;
        UtcNow = utcNow ?? (() => DateTime.UtcNow);
        DiagnosticsEnabled = diagnosticsEnabled;
        Ledger = new RequestLedger();
        Events = new EventHub(log, UtcNow);
        Renames = new RenameMap();
        Owned = new OwnedDocuments();
    }

    public string AddinVersion { get; }
    public int ProcessId { get; }
    public ILog Log { get; }
    public Func<DateTime> UtcNow { get; }

    /// <summary>True when diag.* operations may run (HKCU\Software\solidworks-mcp\Diagnostics = 1 at load).</summary>
    public bool DiagnosticsEnabled { get; }

    public RequestLedger Ledger { get; }
    public EventHub Events { get; }

    /// <summary>Documents saved under a new name while the add-in is loaded, so their references still resolve.</summary>
    public RenameMap Renames { get; }

    /// <summary>The documents the connected client opened or created; cleared when it disconnects.</summary>
    public OwnedDocuments Owned { get; }

    /// <summary>
    /// A document is open under <paramref name="key"/>, freshly opened or created: references to that key name it (any
    /// earlier rename of the key is forgotten), and when <paramref name="owned"/> the client may close it.
    /// </summary>
    public void DocumentOpened(string key, bool owned)
    {
        Renames.Forget(key);
        if (owned) Owned.Add(key);
    }

    /// <summary>A document was saved under a new name: its references follow it, and so does the client's ownership.</summary>
    public void DocumentRenamed(string oldKey, string newKey)
    {
        Renames.Record(oldKey, newKey);
        Owned.Rename(oldKey, newKey);
    }

    /// <summary>Set by the add-in once SOLIDWORKS can be read; null in tests that do not need it.</summary>
    public DocumentTracker? Documents { get; set; }

    /// <summary>Set by the add-in; null when preferences are not managed (tests).</summary>
    public PreferenceGuard? Preferences { get; set; }

    /// <summary>Set by the add-in: raises selection.changed for the user's selections. Null in tests that do not need it.</summary>
    public SelectionReporter? Selection { get; set; }

    /// <summary>SOLIDWORKS' main frame window, read on the UI thread at load; zero when unknown (then no dialog watch).</summary>
    public IntPtr MainWindow { get; set; }

    DiagDialogRequest? _armed;

    /// <summary>diag.arm_dialog: the message box the next model change shows mid-change.</summary>
    public void ArmDialog(DiagDialogRequest box) => System.Threading.Interlocked.Exchange(ref _armed, box);

    /// <summary>The armed message box, once: null when none is armed, and null again after it is taken.</summary>
    public DiagDialogRequest? TakeArmedDialog() => System.Threading.Interlocked.Exchange(ref _armed, null);

    int _interferenceDelayMs;
    long _interferenceOpened, _interferenceClosed;

    /// <summary>diag.interference: the next interference session is held open this long before it is read.</summary>
    public void ArmInterferenceDelay(int ms) => System.Threading.Interlocked.Exchange(ref _interferenceDelayMs, ms);

    /// <summary>The armed delay, once: 0 when none, and 0 again after it is taken.</summary>
    public int TakeInterferenceDelay() => System.Threading.Interlocked.Exchange(ref _interferenceDelayMs, 0);

    public int ArmedInterferenceDelay => System.Threading.Volatile.Read(ref _interferenceDelayMs);

    /// <summary>assembly.interference opened a session (IAssemblyDoc.InterferenceDetectionManager).</summary>
    public void InterferenceOpened() => System.Threading.Interlocked.Increment(ref _interferenceOpened);

    /// <summary>assembly.interference called Done on a session.</summary>
    public void InterferenceClosed() => System.Threading.Interlocked.Increment(ref _interferenceClosed);

    public long InterferenceSessionsOpened => System.Threading.Interlocked.Read(ref _interferenceOpened);

    public long InterferenceSessionsClosed => System.Threading.Interlocked.Read(ref _interferenceClosed);

    /// <summary>Set by the host when <see cref="MainWindow"/> is known; null otherwise.</summary>
    public DialogWatch? Dialogs { get; set; }

    /// <summary>Wired by the host to the dispatcher.</summary>
    public Func<DateTime?> LastPumpedUtc { get; set; } = () => null;

    /// <summary>Wired by the host to the watchdog.</summary>
    public Func<bool> WatchdogAlive { get; set; } = () => false;
}
