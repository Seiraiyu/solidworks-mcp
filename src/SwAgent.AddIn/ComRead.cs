using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace SwAgent.AddIn;

/// <summary>
/// Reads an object SOLIDWORKS returns that the interop declares no interface for, in-process, through its own IDispatch
/// type information: its interface name, then each parameterless property getter, and each parameterless function whose
/// name starts with Get, with its value. Diagnostic only (3.D live run: GetFastenerTable's rows printed as
/// System.__ComObject); UI thread, inside an operation; never throws.
/// </summary>
internal static class ComRead
{
    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatchInfo
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, int lcid, out ComTypes.ITypeInfo info);
    }

    /// <summary>An array's items, or the one value, as text; a COM object as its interface name.</summary>
    public static string[] Items(object? o) => o switch
    {
        null => new string[0],
        Array a => a.Cast<object?>().Select(x => x is Array inner ? "[" + string.Join(", ", Items(inner)) + "]" : Text(x)).ToArray(),
        _ => new[] { Text(o) },
    };

    static string Text(object? x) => x == null ? "null" : Marshal.IsComObject(x) ? $"<{TypeName(x)}>" : Convert.ToString(x, CultureInfo.InvariantCulture) ?? "";

    /// <summary>The value read: arrays item by item, COM objects member by member, to <paramref name="depth"/> levels.</summary>
    public static string Describe(object? o, int depth)
    {
        try
        {
            switch (o)
            {
                case null:
                    return "null";
                case string s:
                    return s;
                case Array a:
                    return "[" + string.Join(" | ", a.Cast<object?>().Take(60).Select(x => Describe(x, depth))) + (a.Length > 60 ? $" | … {a.Length} items" : "") + "]";
            }
            if (!Marshal.IsComObject(o)) return Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
            string name = TypeName(o);
            if (depth <= 0) return $"<{name}>";
            var parts = new List<string>();
            foreach (var (member, isGetter) in Members(o))
            {
                object? value;
                try
                {
                    value = o.GetType().InvokeMember(member, isGetter ? BindingFlags.GetProperty : BindingFlags.InvokeMethod, null, o, null, CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    value = $"({(e.InnerException ?? e).GetType().Name})";
                }
                parts.Add($"{member}={Describe(value, depth - 1)}");
                if (parts.Count >= 30) break;
            }
            return $"<{name}>{{{string.Join(", ", parts)}}}";
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.GetType().Name}: {e.Message})";
        }
    }

    static string TypeName(object o)
    {
        var info = Info(o);
        if (info == null) return "no type info";
        info.GetDocumentation(-1, out string name, out _, out _, out _);
        return name;
    }

    static ComTypes.ITypeInfo? Info(object o)
    {
        if (!(o is IDispatchInfo dispatch)) return null;
        if (dispatch.GetTypeInfoCount(out uint count) != 0 || count == 0) return null;
        return dispatch.GetTypeInfo(0, 0, out var info) == 0 ? info : null;
    }

    /// <summary>The parameterless getters, and parameterless functions named Get…, that the object's type info lists.</summary>
    static IEnumerable<(string Name, bool IsGetter)> Members(object o)
    {
        var info = Info(o);
        if (info == null) yield break;
        info.GetTypeAttr(out IntPtr attrPtr);
        var attr = Marshal.PtrToStructure<ComTypes.TYPEATTR>(attrPtr);
        info.ReleaseTypeAttr(attrPtr);
        var seen = new HashSet<string>();
        for (int i = 0; i < attr.cFuncs; i++)
        {
            info.GetFuncDesc(i, out IntPtr descPtr);
            var desc = Marshal.PtrToStructure<ComTypes.FUNCDESC>(descPtr);
            info.ReleaseFuncDesc(descPtr);
            if (desc.cParams != 0 || (desc.wFuncFlags & (short)ComTypes.FUNCFLAGS.FUNCFLAG_FRESTRICTED) != 0) continue;
            info.GetDocumentation(desc.memid, out string name, out _, out _, out _);
            bool getter = desc.invkind == ComTypes.INVOKEKIND.INVOKE_PROPERTYGET;
            bool function = desc.invkind == ComTypes.INVOKEKIND.INVOKE_FUNC && name.StartsWith("Get", StringComparison.Ordinal);
            if ((getter || function) && seen.Add(name)) yield return (name, getter);
        }
    }
}
