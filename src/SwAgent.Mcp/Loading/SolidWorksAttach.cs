using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp;

/// <summary>Asks a running SOLIDWORKS to load the add-in. Attaches only; never starts SOLIDWORKS.</summary>
public interface ISolidWorksAttach
{
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
            throw new SwAgentException(ErrorCode.ApiError,
                $"SOLIDWORKS (process {processId}) failed ISldWorks.LoadAddIn: {inner.Message}",
                new JsonObject { ["process_id"] = (long)processId, ["path"] = dllPath });
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
