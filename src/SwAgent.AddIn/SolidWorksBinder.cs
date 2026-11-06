using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>
/// api.call's binder for SOLIDWORKS' own COM objects. An object's interface is the name its IDispatch type information
/// gives; a member's signature (its spelling, and which parameters are ref or out) is read from SOLIDWORKS' interop for
/// that interface, falling back to ISldWorks and IModelDoc2 for SOLIDWORKS and documents. The call itself goes through
/// IDispatch (<see cref="LateBinder.Invoke"/>). Covered by the parts live suite, not by unit tests.
/// </summary>
internal sealed class SolidWorksBinder : LateBinder
{
    static readonly Assembly Interop = typeof(ISldWorks).Assembly;

    /// <summary>IDispatch's first two methods, enough to read an object's type information.</summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDispatchTypeInfo
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint lcid, out System.Runtime.InteropServices.ComTypes.ITypeInfo? info);
    }

    public override ApiObjectKind Classify(object target) =>
        target is ISldWorks ? ApiObjectKind.App : target is IModelDoc2 ? ApiObjectKind.Document : ApiObjectKind.Other;

    public override string InterfaceName(object target)
    {
        if (!Marshal.IsComObject(target)) return base.InterfaceName(target);
        try
        {
            if (((IDispatchTypeInfo)target).GetTypeInfo(0, 0, out var info) != 0 || info == null) return "IDispatch";
            info.GetDocumentation(-1, out string name, out _, out _, out _);
            return string.IsNullOrEmpty(name) ? "IDispatch" : name;
        }
        catch (Exception)
        {
            return "IDispatch";
        }
    }

    public override ApiSignature? Signature(object target, string member)
    {
        foreach (var type in Candidates(target))
        {
            var signature = ApiSignature.Find(type, member);
            if (signature != null) return signature;
        }
        return null;
    }

    /// <summary>Each array element wrapped for IDispatch, as SOLIDWORKS' API takes arrays of objects.</summary>
    public override object ObjectArray(object[] items) => items.Select(o => (object)new DispatchWrapper(o)).ToArray();

    IEnumerable<Type> Candidates(object target)
    {
        string name = InterfaceName(target);
        foreach (var n in new[] { name, "I" + name })
        {
            var type = Interop.GetType("SolidWorks.Interop.sldworks." + n);
            if (type != null && type.IsInterface) yield return type;
        }
        switch (Classify(target))
        {
            case ApiObjectKind.App:
                yield return typeof(ISldWorks);
                break;
            case ApiObjectKind.Document:
                yield return typeof(IModelDoc2);
                if (target is IPartDoc) yield return typeof(IPartDoc);
                break;
        }
    }
}
