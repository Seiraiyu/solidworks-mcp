using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// The add-in SOLIDWORKS loads. ConnectToSW starts the agent host (operations, dispatcher, pipe) on SOLIDWORKS'
/// UI thread; DisconnectFromSW stops it. Neither lets an exception reach SOLIDWORKS.
/// </summary>
[ComVisible(true)]
[Guid(AddInIdentity.Clsid)]
[ProgId(AddInIdentity.ProgId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SwAgentAddIn : ISwAddin
{
    ISldWorks? _sw;
    FileLog? _log;
    AgentHost? _host;
    SolidWorksEventBridge? _events;
    PreferenceGuard? _preferences;
    AgentRuntime? _runtime;

    /// <summary>The informational version: the package version and, from a CI or git build, the commit.</summary>
    public static string Version =>
        typeof(SwAgentAddIn).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SwAgentAddIn).Assembly.GetName().Version!.ToString();

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        try
        {
            _log = new FileLog(FileLog.DefaultDirectory, "addin");
            _sw = (ISldWorks)ThisSW;
            var app = new SolidWorksApplication(_sw);
            int pid = Process.GetCurrentProcess().Id;
            _log.Info($"loading add-in {Version} into SOLIDWORKS {app.RevisionNumber} (process {pid}, CLR {System.Environment.Version}, cookie {Cookie})");
            var runtime = new AgentRuntime(Version, pid, _log, diagnosticsEnabled: DiagnosticsSetting.Enabled(Microsoft.Win32.Registry.CurrentUser));
            _runtime = runtime;
            runtime.MainWindow = FrameWindow(_sw, _log);
            _preferences = new PreferenceGuard(new SolidWorksPreferenceStore(_sw), AutomationPreferences.All, PreferenceGuard.DefaultJournalPath, _log);
            try
            {
                _preferences.Recover();
            }
            catch (Exception e)
            {
                // The guard catches its own failures; this is the last line, so one COM failure cannot stop the add-in loading.
                _log.Error("preferences: recovery at load failed; the journal (if any) is kept", e);
            }
            runtime.Preferences = _preferences;
            // export.part's preferences are set for one call at a time; a crash mid-export leaves a journal, undone here.
            var exportPreferences = new ScopedPreferences(new SolidWorksTypedPreferenceStore(_sw), ScopedPreferences.DefaultJournalPath, _log);
            try
            {
                exportPreferences.Recover();
            }
            catch (Exception e)
            {
                _log.Error("preferences: recovering export preferences at load failed; the journal (if any) is kept", e);
            }
            if (runtime.DiagnosticsEnabled) _log.Warn("diagnostic operations are ON (HKCU\\Software\\solidworks-mcp\\Diagnostics = 1)");
            var services = new AddInServices(app, Version, runtime) { Preferences = _preferences, ExportPreferences = exportPreferences };
            runtime.Documents = new DocumentTracker(app.OpenDocuments, runtime.Events, _log);
            // The selection is read on the UI thread, between requests, once it has been still for 250 ms.
            runtime.Selection = new SelectionReporter(runtime.Events, () => runtime.Ledger.Running != null, _log,
                read => _host?.Dispatcher.Post("selection.changed", read));
            var registry = CreateRegistry(services);
            _host = AgentHost.Start(Protocol.PipeName(pid), registry, runtime);
            var preferences = _preferences;
            _host.ClientDisconnected = () =>
            {
                // The next client is a new agent: the documents this one opened are no longer its to close.
                runtime.Owned.Clear();
                preferences.Restore("the client disconnected");
            };
            _events = new SolidWorksEventBridge(_sw, runtime.Documents, _log, runtime, services.References);
            _events.Start();
            _log.Info("add-in loaded");
            return true;
        }
        catch (Exception e)
        {
            try
            {
                _log?.Error("the add-in could not load; SOLIDWORKS will report it as not loaded", e);
                Teardown();
            }
            catch (Exception)
            {
                // Nothing may reach SOLIDWORKS.
            }
            return false;
        }
    }

    public bool DisconnectFromSW()
    {
        try
        {
            _log?.Info("unloading add-in");
            _runtime?.Events.Publish("session.closing", new SessionClosingEvent { Reason = "SOLIDWORKS is exiting or the add-in was turned off" });
            Teardown();
        }
        catch (Exception)
        {
            // Nothing may reach SOLIDWORKS.
        }
        return true;
    }

    /// <summary>Exactly one operation per contract: the add-in's own (built with the services) and the core's (built with the runtime).</summary>
    internal static OperationRegistry CreateRegistry(AddInServices services) =>
        OperationRegistry.Discover(OperationCatalog.All,
            typeof(SwAgentAddIn).Assembly.GetTypes().Concat(typeof(AgentRuntime).Assembly.GetTypes()),
            type => type.GetConstructor(new[] { typeof(AgentRuntime) }) != null
                ? (IOperation)Activator.CreateInstance(type, services.Runtime)
                : (IOperation)Activator.CreateInstance(type, services));

    /// <summary>SOLIDWORKS' main frame window, for the dialog watch; zero (watch off) when it cannot be read.</summary>
    static IntPtr FrameWindow(ISldWorks sw, ILog log)
    {
        try
        {
            if (sw.Frame() is IFrame frame) return new IntPtr(frame.GetHWndx64());
            log.Warn("dialogs: SOLIDWORKS returned no frame");
        }
        catch (Exception e)
        {
            log.Error("dialogs: reading SOLIDWORKS' main window failed", e);
        }
        return IntPtr.Zero;
    }

    void Teardown()
    {
        try
        {
            _events?.Dispose();
        }
        catch (Exception e)
        {
            _log?.Error("unsubscribing SOLIDWORKS events failed", e);
        }
        _events = null;
        _runtime?.Selection?.Dispose();
        try
        {
            _preferences?.Restore("the add-in is unloading");
        }
        catch (Exception e)
        {
            _log?.Error("restoring preferences failed; the journal is kept for the next load", e);
        }
        _preferences = null;
        _runtime = null;
        var host = _host;
        _host = null;
        try
        {
            host?.Dispose();
        }
        catch (Exception e)
        {
            _log?.Error("stopping the agent host failed", e);
        }
        if (_sw != null)
        {
            Marshal.ReleaseComObject(_sw);
            _sw = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        _log?.Info("add-in unloaded");
        _log?.Dispose();
        _log = null;
    }
}
