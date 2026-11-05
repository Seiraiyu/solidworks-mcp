using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The SP4 canary (design 356 and 363): one sketch line created through COM from outside SOLIDWORKS, without
/// CommandInProgress, which crashed SOLIDWORKS 2026 SP4 every time it was measured. It is expected to crash SOLIDWORKS.
/// It is the only code in this project that makes a model-changing COM call from outside the UI thread, and it exists
/// only to notice when a SOLIDWORKS update changes that. Never part of the default run: it needs SWMCP_LIVE=1 and
/// SWMCP_CANARY=1, and is run on its own (dotnet test --filter FullyQualifiedName~Canary) after a SOLIDWORKS update,
/// with the owner's go-ahead, on a SOLIDWORKS with no documents open.
/// </summary>
public sealed class Canary : IClassFixture<CanaryRun>
{
    readonly LiveRun _run;

    public Canary(CanaryRun run)
    {
        _run = run;
    }

    [SkippableFact]
    public async Task An_external_sketch_call_still_crashes_SOLIDWORKS()
    {
        LiveSettings.RequireCanary();
        await _run.CaseAsync(1, "canary: an external sketch call without CommandInProgress crashes SOLIDWORKS", async () =>
        {
            var status = await _run.Supervisor.StatusAsync(TimeSpan.FromSeconds(60));
            if (status.State != SupervisorState.Ready) throw _run.Stop($"SOLIDWORKS is not ready ({status.Message})");
            int pid = status.ProcessId!.Value;
            _run.SwRevision = status.Session?.SwRevision;
            // Opened first: once SOLIDWORKS has crashed there is no process left to look up.
            using var process = Process.GetProcessById(pid);

            // The guard's check, immediately before drawing: only an empty SOLIDWORKS, with no dialog, is crashed.
            var state = await _run.Guard.CheckAsync(pid, _run.AddinStateAsync);
            if (state.Documents.Count > 0) throw new SkipException("documents are open; the canary runs only on an empty SOLIDWORKS");

            _run.Say($"canary: drawing one line through external COM in SOLIDWORKS {pid}; it is expected to crash");
            var clock = Stopwatch.StartNew();
            string call;
            try
            {
                await StaCall.RunAsync("the canary sketch", () => ExternalSketch.DrawLine(pid), TimeSpan.FromSeconds(60));
                call = "CreateLine returned";
            }
            catch (Exception e) when (e is COMException || e is TargetInvocationException || e is InvalidComObjectException || e is SwAgentException)
            {
                // SOLIDWORKS going away under the call disconnects it: that is the crash, not a failure of the canary.
                call = $"the COM call ended as SOLIDWORKS went ({(e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message)})";
            }
            bool died = process.WaitForExit(30_000);
            Assert.True(died, $"SOLIDWORKS {pid} ({_run.SwRevision}) survived an external sketch call for 30 s ({call}): the SP4 crash may be fixed in this revision. " +
                "Repeat the measurement in design 356 before relying on it, and close the canary's untitled part without saving.");
            return $"SOLIDWORKS {pid} ({_run.SwRevision}): {call}; the process exited {clock.ElapsedMilliseconds} ms after the call started (exit code {process.ExitCode}): the constraint still holds";
        });
    }
}

/// <summary>The canary's run: its own id (…-canary), directory and report, apart from the fault suite's.</summary>
public sealed class CanaryRun : LiveRun
{
    public CanaryRun()
        : base("canary")
    {
    }
}

/// <summary>The canary's own out-of-process COM, by late binding: the measured SP4 reproduction, nothing more.</summary>
static class ExternalSketch
{
    const int DefaultTemplatePart = 8; // swUserPreferenceStringValue_e.swDefaultTemplatePart

    public static int DrawLine(int processId)
    {
        if (CLSIDFromProgID("SldWorks.Application", out Guid clsid) != 0 || GetActiveObject(ref clsid, IntPtr.Zero, out object? sw) != 0 || sw == null)
            throw new InvalidOperationException("no SOLIDWORKS is registered as the active SldWorks.Application");
        int active = Convert.ToInt32(Invoke(sw, "GetProcessID"), CultureInfo.InvariantCulture);
        if (active != processId) throw new InvalidOperationException($"the active SOLIDWORKS is process {active}, not {processId}; nothing was drawn");
        string template = (string)Invoke(sw, "GetUserPreferenceStringValue", DefaultTemplatePart)!;
        object doc = Invoke(sw, "NewDocument", template, 0, 0.0, 0.0) ?? throw new InvalidOperationException($"SOLIDWORKS did not create a part from {template}");
        object extension = Get(doc, "Extension");
        Invoke(extension, "SelectByID2", "Front Plane", "PLANE", 0.0, 0.0, 0.0, false, 0, new DispatchWrapper(null), 0);
        object sketches = Get(doc, "SketchManager");
        Invoke(sketches, "InsertSketch", true);
        Invoke(sketches, "CreateLine", 0.0, 0.0, 0.0, 0.05, 0.02, 0.0);
        return processId;
    }

    static object Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"SOLIDWORKS returned no {property}");

    static object? Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? active);
}
