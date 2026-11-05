using System;

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
    }

    public string AddinVersion { get; }
    public int ProcessId { get; }
    public ILog Log { get; }
    public Func<DateTime> UtcNow { get; }

    /// <summary>True when diag.* operations may run (HKCU\Software\solidworks-mcp\Diagnostics = 1 at load).</summary>
    public bool DiagnosticsEnabled { get; }

    public RequestLedger Ledger { get; }
    public EventHub Events { get; }

    /// <summary>Set by the add-in once SOLIDWORKS can be read; null in tests that do not need it.</summary>
    public DocumentTracker? Documents { get; set; }

    /// <summary>Set by the add-in; null when preferences are not managed (tests).</summary>
    public PreferenceGuard? Preferences { get; set; }

    /// <summary>SOLIDWORKS' main frame window, read on the UI thread at load; zero when unknown (then no dialog watch).</summary>
    public IntPtr MainWindow { get; set; }

    /// <summary>Set by the host when <see cref="MainWindow"/> is known; null otherwise.</summary>
    public DialogWatch? Dialogs { get; set; }

    /// <summary>Wired by the host to the dispatcher.</summary>
    public Func<DateTime?> LastPumpedUtc { get; set; } = () => null;

    /// <summary>Wired by the host to the watchdog.</summary>
    public Func<bool> WatchdogAlive { get; set; } = () => false;
}
