using System;
using System.Diagnostics;
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
            var services = new AddInServices(app, Version);
            var registry = CreateRegistry(services);
            _host = AgentHost.Start(Protocol.PipeName(pid), registry, _log);
            var dispatcher = _host.Dispatcher;
            services.QueuedRequests = () => dispatcher.QueuedRequests;
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
            Teardown();
        }
        catch (Exception)
        {
            // Nothing may reach SOLIDWORKS.
        }
        return true;
    }

    /// <summary>Exactly one operation per contract, each built with <paramref name="services"/>.</summary>
    internal static OperationRegistry CreateRegistry(AddInServices services) =>
        OperationRegistry.Discover(OperationCatalog.All, typeof(SwAgentAddIn).Assembly.GetTypes(),
            type => (IOperation)Activator.CreateInstance(type, services));

    void Teardown()
    {
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
