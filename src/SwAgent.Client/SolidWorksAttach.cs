using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

/// <summary>Whether a SOLIDWORKS process has finished starting, and if not, what it lacks.</summary>
public sealed class SolidWorksReadiness
{
    public SolidWorksReadiness(bool ready, string reason)
    {
        Ready = ready;
        Reason = reason;
    }

    public static readonly SolidWorksReadiness Yes = new SolidWorksReadiness(true, "ready");

    public bool Ready { get; }

    /// <summary>What it lacks, for the sw_starting message; "ready" when ready.</summary>
    public string Reason { get; }
}

/// <summary>
/// The only things swmcp does to a SOLIDWORKS from outside its process: find out whether it has finished starting,
/// whether Windows thinks it is hung, and ask it to load the add-in. Attaches only; never starts SOLIDWORKS.
/// </summary>
public interface ISolidWorksAttach
{
    /// <summary>
    /// Ready when the process shows a visible main window titled SOLIDWORKS and is in the Running Object Table. A
    /// SOLIDWORKS showing its splash, or waiting at a 3DEXPERIENCE sign-in or update prompt, is not. The only COM call
    /// is GetProcessID, to tell ROT entries apart.
    /// </summary>
    SolidWorksReadiness Probe(int processId);

    /// <summary>True when Windows reports the process's main window hung (IsHungAppWindow); false when it has none.</summary>
    bool IsHung(int processId);

    /// <summary>Attaches to SOLIDWORKS process <paramref name="processId"/> and calls <c>ISldWorks.LoadAddIn(dllPath)</c>; returns its result.</summary>
    int LoadAddIn(int processId, string dllPath);
}

/// <summary>
/// The real attach, through COM late binding (IDispatch) so swmcp takes no SOLIDWORKS interop dependency. Finds the
/// instance in the Running Object Table by its process id, falling back to the active <c>SldWorks.Application</c>
/// when that is the same process. Never CoCreateInstance: that would start a SOLIDWORKS outside the launcher.
/// </summary>
public sealed class ComSolidWorksAttach : ISolidWorksAttach
{
    public static readonly ComSolidWorksAttach Instance = new ComSolidWorksAttach();

    const string ProgId = "SldWorks.Application";

    /// <summary>SOLIDWORKS' main window title starts with this; its splash and the Welcome dialog do not.</summary>
    public const string MainWindowTitlePrefix = "SOLIDWORKS";

    public SolidWorksReadiness Probe(int processId)
    {
        if (MainWindow(processId) == IntPtr.Zero)
            return new SolidWorksReadiness(false, "it shows no SOLIDWORKS main window yet (splash, or a 3DEXPERIENCE sign-in or update prompt)");
        object? found = FromRunningObjectTable(processId);
        if (found == null) return new SolidWorksReadiness(false, "it is not in the Running Object Table yet");
        Marshal.ReleaseComObject(found);
        return SolidWorksReadiness.Yes;
    }

    public bool IsHung(int processId)
    {
        var window = MainWindow(processId);
        return window != IntPtr.Zero && IsHungAppWindow(window);
    }

    /// <summary>The process's visible, unowned top-level window whose title starts with SOLIDWORKS; zero when none.</summary>
    public static IntPtr MainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindowsProc visit = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)processId || !IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (!title.ToString().StartsWith(MainWindowTitlePrefix, StringComparison.OrdinalIgnoreCase)) return true;
            found = hwnd;
            return false;
        };
        EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        return found;
    }

    const int GwOwner = 4;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    static extern bool IsHungAppWindow(IntPtr hwnd);

    public int LoadAddIn(int processId, string dllPath)
    {
        object sw = Attach(processId);
        try
        {
            object? result = Invoke(sw, "LoadAddIn", dllPath);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is COMException || e is TargetInvocationException || e is InvalidCastException || e is FormatException)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            string hresult = Hresult(inner.HResult);
            throw new SwAgentException(ErrorCode.ApiError,
                $"SOLIDWORKS (process {processId}) failed ISldWorks.LoadAddIn ({hresult}): {inner.Message}",
                new JsonObject { ["process_id"] = (long)processId, ["path"] = dllPath, ["hresult"] = hresult });
        }
        finally
        {
            Marshal.ReleaseComObject(sw);
        }
    }

    static object Attach(int processId)
    {
        object? found = FromRunningObjectTable(processId) ?? FromActiveObject(processId);
        return found ?? throw new SwAgentException(ErrorCode.ApiError,
            $"could not attach to SOLIDWORKS process {processId}: it is not in this session's Running Object Table " +
            "(is it still starting, or running elevated?); wait for its main window and try again",
            new JsonObject { ["process_id"] = (long)processId });
    }

    /// <summary>SOLIDWORKS registers each instance in the ROT; the one whose GetProcessID matches is ours.</summary>
    static object? FromRunningObjectTable(int processId)
    {
        if (GetRunningObjectTable(0, out var rot) != 0 || rot == null) return null;
        if (CreateBindCtx(0, out var context) != 0 || context == null) return null;
        rot.EnumRunning(out var monikers);
        var one = new IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            string name;
            try
            {
                one[0].GetDisplayName(context, null, out name);
            }
            catch (COMException)
            {
                continue;
            }
            if (name.IndexOf("SolidWorks", StringComparison.OrdinalIgnoreCase) < 0) continue;
            object? candidate = null;
            try
            {
                rot.GetObject(one[0], out candidate);
                if (candidate != null && ProcessIdOf(candidate) == processId) return candidate;
            }
            catch (Exception e) when (e is COMException || e is TargetInvocationException || e is MissingMethodException || e is InvalidCastException)
            {
                // Not a SOLIDWORKS application object, or it went away.
            }
            if (candidate != null) Marshal.ReleaseComObject(candidate);
        }
        return null;
    }

    static object? FromActiveObject(int processId)
    {
        if (CLSIDFromProgID(ProgId, out Guid clsid) != 0) return null;
        if (GetActiveObject(ref clsid, IntPtr.Zero, out object? active) != 0 || active == null) return null;
        try
        {
            if (ProcessIdOf(active) == processId) return active;
        }
        catch (Exception e) when (e is COMException || e is TargetInvocationException || e is InvalidCastException)
        {
            // Fall through: not usable.
        }
        Marshal.ReleaseComObject(active);
        return null;
    }

    /// <summary>An HRESULT as COM documentation writes it, for example 0x80010001.</summary>
    public static string Hresult(int hresult) => "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);

    static int ProcessIdOf(object sw) => Convert.ToInt32(Invoke(sw, "GetProcessID"), CultureInfo.InvariantCulture);

    static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    [DllImport("ole32.dll")]
    static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable? rot);

    [DllImport("ole32.dll")]
    static extern int CreateBindCtx(int reserved, out IBindCtx? context);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll")]
    static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? active);
}
