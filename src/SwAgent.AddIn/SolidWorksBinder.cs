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
/// gives; when that gives none (as on seiraiyu2 in 3.E's live run, round 1, where every object read IDispatch), it is
/// the first of the common interop interfaces the object answers QueryInterface for, and what the type information
/// returned is logged once. A member's signature (its spelling, and which parameters are ref or out) is read from
/// SOLIDWORKS' interop: the named interface, then ISldWorks or IModelDoc2 for SOLIDWORKS and documents, then every
/// interop interface that declares the member and that the object answers QueryInterface for. Everything here is in
/// process, in the add-in; nothing is called on the object but QueryInterface and IDispatch's GetTypeInfo. The call
/// itself goes through IDispatch (<see cref="LateBinder.Invoke"/>). Covered by the parts live suite.
/// </summary>
internal sealed class SolidWorksBinder : LateBinder
{
    static readonly Assembly Interop = typeof(ISldWorks).Assembly;

    /// <summary>The interfaces an object is named by when its type information names none, most specific first.</summary>
    static readonly Type[] Named =
    {
        typeof(ISldWorks), typeof(IModelDoc2), typeof(IModelDocExtension), typeof(IFeatureManager), typeof(ISketchManager),
        typeof(ISelectionMgr), typeof(IConfigurationManager), typeof(IConfiguration), typeof(IFeature), typeof(IFace2),
        typeof(IEdge), typeof(IVertex), typeof(IBody2), typeof(ISketch), typeof(ISketchSegment), typeof(ISketchPoint),
        typeof(IDimension), typeof(IDisplayDimension), typeof(IRefPlane), typeof(IRefAxis), typeof(IRefPoint),
        typeof(IComponent2), typeof(IMathUtility),
    };

    /// <summary>Every interop interface, by the members it declares (any case).</summary>
    static readonly Lazy<Dictionary<string, Type[]>> Declaring = new Lazy<Dictionary<string, Type[]>>(() =>
        Interop.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.IsSpecialName).Select(m => m.Name)
                .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => (Name: n, Type: t)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Type).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase));

    readonly ILog _log;
    readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

    public SolidWorksBinder(ILog log)
    {
        _log = log;
    }

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

    /// <summary>The interop interfaces that declare <paramref name="member"/> (any case), by name.</summary>
    internal static IReadOnlyList<Type> InterfacesDeclaring(string member) =>
        Declaring.Value.TryGetValue(member, out var types) ? types : Array.Empty<Type>();

    public override ApiObjectKind Classify(object target) =>
        target is ISldWorks ? ApiObjectKind.App : target is IModelDoc2 ? ApiObjectKind.Document : ApiObjectKind.Other;

    public override string InterfaceName(object target)
    {
        if (!Marshal.IsComObject(target)) return base.InterfaceName(target);
        string? name = TypeInfoName(target, out string what);
        if (name != null) return name;
        var named = Named.FirstOrDefault(t => t.IsInstanceOfType(target));
        Report($"api.call: IDispatch type information named no interface ({what}); {(named == null ? "no common interop interface answers QueryInterface either, so it is IDispatch" : $"named {named.Name} by QueryInterface")}");
        return named?.Name ?? "IDispatch";
    }

    public override ApiSignature? Signature(object target, string member)
    {
        foreach (var type in Candidates(target, member))
        {
            var signature = ApiSignature.Find(type, member);
            if (signature != null) return signature;
        }
        return null;
    }

    /// <summary>Each array element wrapped for IDispatch, as SOLIDWORKS' API takes arrays of objects.</summary>
    public override object ObjectArray(object[] items) => items.Select(o => (object)new DispatchWrapper(o)).ToArray();

    /// <summary>The name the type information gives, or null with what it gave instead in <paramref name="what"/>.</summary>
    static string? TypeInfoName(object target, out string what)
    {
        try
        {
            var dispatch = (IDispatchTypeInfo)target;
            int countHr = dispatch.GetTypeInfoCount(out uint count);
            int hr = dispatch.GetTypeInfo(0, 0, out var info);
            if (hr != 0 || info == null)
            {
                what = $"GetTypeInfoCount 0x{countHr:X8} count {count}, GetTypeInfo 0x{hr:X8}{(info == null ? " with no type information" : "")}";
                return null;
            }
            info.GetDocumentation(-1, out string name, out _, out _, out _);
            if (string.IsNullOrEmpty(name) || string.Equals(name, "IDispatch", StringComparison.OrdinalIgnoreCase))
            {
                what = $"GetTypeInfoCount 0x{countHr:X8} count {count}, GetTypeInfo 0x00000000 naming '{name}'";
                return null;
            }
            what = "";
            return name;
        }
        catch (Exception e)
        {
            what = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    IEnumerable<Type> Candidates(object target, string member)
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
        // The interfaces that declare the member, if the object is one of them.
        foreach (var type in InterfacesDeclaring(member))
        {
            if (type.IsInstanceOfType(target)) yield return type;
        }
    }

    void Report(string message)
    {
        lock (_reported)
        {
            if (!_reported.Add(message)) return;
        }
        _log.Warn(message);
    }
}
