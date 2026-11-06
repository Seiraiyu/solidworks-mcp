using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>A parameter of a member as SOLIDWORKS' interop declares it.</summary>
public sealed class ApiParameter
{
    public ApiParameter(string name, Type type, bool byRef, bool isOut)
    {
        Name = name;
        Type = type;
        ByRef = byRef;
        IsOut = isOut;
    }

    public string Name { get; }

    /// <summary>The parameter's type; for a ref or out parameter, the type it refers to.</summary>
    public Type Type { get; }

    /// <summary>A ref or out parameter: its value after the call is returned in out.</summary>
    public bool ByRef { get; }
    public bool IsOut { get; }

    public override string ToString() => $"{(IsOut ? "out " : ByRef ? "ref " : "")}{TypeName(Type)} {Name}";

    internal static string TypeName(Type t) =>
        t == typeof(int) ? "int" : t == typeof(double) ? "double" : t == typeof(bool) ? "bool" : t == typeof(string) ? "string"
        : t == typeof(object) ? "object" : t == typeof(short) ? "short" : t == typeof(long) ? "long" : t.Name;
}

/// <summary>A member's name as SOLIDWORKS spells it and its parameters, when the interop declares it.</summary>
public sealed class ApiSignature
{
    public ApiSignature(string name, IReadOnlyList<ApiParameter> parameters)
    {
        Name = name;
        Parameters = parameters;
    }

    public string Name { get; }
    public IReadOnlyList<ApiParameter> Parameters { get; }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";

    /// <summary>The method or property of <paramref name="type"/> named <paramref name="member"/> (any case), or null.</summary>
    public static ApiSignature? Find(Type type, string member)
    {
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => !m.IsSpecialName && string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase));
        if (method != null) return new ApiSignature(method.Name, method.GetParameters().Select(Of).ToList());
        var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase));
        return property == null ? null : new ApiSignature(property.Name, property.GetIndexParameters().Select(Of).ToList());
    }

    static ApiParameter Of(ParameterInfo p)
    {
        bool byRef = p.ParameterType.IsByRef;
        return new ApiParameter(p.Name ?? "arg" + p.Position, byRef ? p.ParameterType.GetElementType()! : p.ParameterType, byRef, p.IsOut);
    }
}

/// <summary>
/// How api.call reaches a member (design 371, Escape hatch): late binding through IDispatch with
/// <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[], ParameterModifier[], CultureInfo, string[])"/>,
/// which works the same on a managed object (the unit tests' fakes) and on a COM object (SOLIDWORKS'). What an object
/// is, its interface's name and a member's signature are virtual: <see cref="SolidWorksBinder"/> reads them from the
/// COM object and SOLIDWORKS' interop.
/// </summary>
public class LateBinder
{
    const int DispUnknownName = unchecked((int)0x80020006);
    const int DispMemberNotFound = unchecked((int)0x80020003);
    static readonly int[] DispBadArguments =
    {
        unchecked((int)0x80020004), // DISP_E_PARAMNOTFOUND
        unchecked((int)0x80020005), // DISP_E_TYPEMISMATCH
        unchecked((int)0x80020008), // DISP_E_BADVARTYPE
        unchecked((int)0x8002000A), // DISP_E_OVERFLOW
        unchecked((int)0x8002000E), // DISP_E_BADPARAMCOUNT
        unchecked((int)0x8002000F), // DISP_E_PARAMNOTOPTIONAL
    };

    /// <summary>SOLIDWORKS itself, a document, or anything else, for the refused-member rules.</summary>
    public virtual ApiObjectKind Classify(object target) => ApiObjectKind.Other;

    /// <summary>The interface an object is known by, for example IModelDocExtension.</summary>
    public virtual string InterfaceName(object target) => target.GetType().Name;

    /// <summary>The member's signature on this object, or null when it is not known.</summary>
    public virtual ApiSignature? Signature(object target, string member) => ApiSignature.Find(target.GetType(), member);

    /// <summary>An array of objects as the API takes one (SOLIDWORKS: each wrapped for IDispatch).</summary>
    public virtual object ObjectArray(object[] items) => items;

    /// <summary>
    /// Calls <paramref name="member"/> on <paramref name="target"/> as a method or a property get; ref and out
    /// arguments are written back into <paramref name="args"/>. invalid_argument for an unknown member or arguments
    /// that do not fit; api_error for what SOLIDWORKS throws.
    /// </summary>
    public object? Invoke(object target, string member, object?[] args, ApiSignature? signature, string path)
    {
        ParameterModifier[]? modifiers = null;
        if (signature != null && args.Length > 0 && signature.Parameters.Any(p => p.ByRef))
        {
            var m = new ParameterModifier(args.Length);
            for (int i = 0; i < args.Length && i < signature.Parameters.Count; i++) m[i] = signature.Parameters[i].ByRef;
            modifiers = new[] { m };
        }
        try
        {
            return target.GetType().InvokeMember(member,
                BindingFlags.InvokeMethod | BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                null, target, args, modifiers, CultureInfo.InvariantCulture, null);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw Thrown(e.InnerException, member, path);
        }
        catch (MissingMemberException)
        {
            throw NoMember(target, member, path);
        }
        catch (COMException e) when (e.ErrorCode == DispUnknownName || e.ErrorCode == DispMemberNotFound)
        {
            throw NoMember(target, member, path);
        }
        catch (COMException e) when (DispBadArguments.Contains(e.ErrorCode))
        {
            throw BadArguments(target, member, signature, $"SOLIDWORKS refused them (0x{e.ErrorCode:X8}: {e.Message})", path);
        }
        catch (COMException e)
        {
            throw Thrown(e, member, path);
        }
        catch (Exception e) when (e is ArgumentException || e is TargetParameterCountException || e is InvalidCastException || e is OverflowException || e is AmbiguousMatchException)
        {
            throw BadArguments(target, member, signature, e.Message, path);
        }
    }

    SwAgentException NoMember(object target, string member, string path) =>
        new SwAgentException(ErrorCode.InvalidArgument, $"{InterfaceName(target)} has no member {member} (in {path}); member names are SOLIDWORKS' API names",
            new JsonObject { ["field"] = "member", ["member"] = path, ["interface"] = InterfaceName(target) });

    SwAgentException BadArguments(object target, string member, ApiSignature? signature, string why, string path) =>
        new SwAgentException(ErrorCode.InvalidArgument,
            $"the arguments do not fit {InterfaceName(target)}.{signature?.ToString() ?? member}: {why}",
            new JsonObject { ["field"] = "args", ["member"] = path, ["signature"] = signature?.ToString() });

    static SwAgentException Thrown(Exception e, string member, string path)
    {
        if (e is SwAgentException own) return own;
        var details = new JsonObject { ["member"] = path, ["exception"] = e.GetType().Name };
        if (e is COMException com) details["hresult"] = $"0x{com.ErrorCode:X8}";
        return new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS failed in {member} (in {path}): {e.Message}", details);
    }
}

/// <summary>What one api.call did: the member path as SOLIDWORKS spells it, the interfaces walked, the value and the ref and out arguments.</summary>
public sealed class ApiCallOutcome
{
    public string Member { get; set; } = "";
    public List<string> Interfaces { get; } = new List<string>();
    public object? Value { get; set; }

    /// <summary>Each ref or out argument after the call, by its parameter's name (or its position when the name is not known).</summary>
    public List<KeyValuePair<string, object?>> Out { get; } = new List<KeyValuePair<string, object?>>();
}

/// <summary>
/// Walks an api.call's dotted member path (design 371, Escape hatch): every segment but the last is a property or a
/// parameterless method, the last is called with the arguments. Every segment is checked against
/// <see cref="ApiCallGuard"/> before anything is called (the refused names), and again on the object it is called on
/// (Close and Quit members of SOLIDWORKS and documents). Arguments are converted from JSON to the parameter types the
/// interop declares, references to the objects already resolved; results are converted back to JSON.
/// </summary>
public static class ApiCaller
{
    public static ApiCallOutcome Call(LateBinder binder, object target, string member, IReadOnlyList<object?> args, IReadOnlyDictionary<string, object> resolved)
    {
        var segments = ApiCallGuard.Split(member);
        ApiCallGuard.CheckNames(segments, member);
        var outcome = new ApiCallOutcome();
        var spelled = new List<string>();
        object current = target;
        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i];
            var signature = binder.Signature(current, segment);
            ApiCallGuard.Check(segment, binder.Classify(current), signature?.Name, member);
            outcome.Interfaces.Add(binder.InterfaceName(current));
            spelled.Add(signature?.Name ?? segment);
            if (i < segments.Count - 1)
            {
                if (signature != null && signature.Parameters.Count > 0)
                {
                    throw new SwAgentException(ErrorCode.InvalidArgument,
                        $"{binder.InterfaceName(current)}.{signature} takes arguments, so it can only be the last member of the path; a chain that needs arguments on the way is an operation to ask for",
                        new JsonObject { ["field"] = "member", ["member"] = member, ["signature"] = signature.ToString() });
                }
                var next = binder.Invoke(current, segment, new object?[0], signature, member);
                if (!IsObject(next))
                {
                    throw new SwAgentException(ErrorCode.InvalidArgument,
                        next == null
                            ? $"{string.Join(".", spelled)} returned nothing, so there is no object to call {segments[i + 1]} on"
                            : $"{string.Join(".", spelled)} is a value ({ApiValues.ToJson(next, _ => null)}), not an object, so only the last member of the path may be it",
                        new JsonObject { ["field"] = "member", ["member"] = member, ["at"] = string.Join(".", spelled) });
                }
                current = next!;
                continue;
            }
            var values = ApiValues.Arguments(binder, args, signature, binder.InterfaceName(current), resolved);
            outcome.Value = binder.Invoke(current, segment, values, signature, member);
            for (int a = 0; a < values.Length; a++)
            {
                if (signature != null && a < signature.Parameters.Count && signature.Parameters[a].ByRef)
                    outcome.Out.Add(new KeyValuePair<string, object?>(signature.Parameters[a].Name, values[a]));
            }
        }
        outcome.Member = string.Join(".", spelled);
        return outcome;
    }

    /// <summary>A COM or managed object a member can be called on: not null, a string, a number, an enum or an array.</summary>
    internal static bool IsObject(object? value) =>
        value != null && !(value is string) && !(value is Array) && !(value is Enum) && !(value is decimal) && !(value is DateTime) && !value.GetType().IsPrimitive;
}

/// <summary>JSON to the API's argument types and the API's values back to JSON, for api.call. SOLIDWORKS' own units: nothing is converted.</summary>
public static class ApiValues
{
    /// <summary>
    /// The reference each <c>{"ref": "r1..."}</c> argument names, in order (arrays included), so all are resolved
    /// before anything is called; invalid_argument for any other object as an argument.
    /// </summary>
    public static IReadOnlyList<(string Reference, string Field)> References(IReadOnlyList<object?> args)
    {
        var found = new List<(string, string)>();
        void Visit(object? value, string field)
        {
            if (value is JsonObject o) found.Add((RefOf(o, field), field));
            else if (value is List<object?> list) for (int i = 0; i < list.Count; i++) Visit(list[i], $"{field}/{i}");
        }
        for (int i = 0; i < args.Count; i++) Visit(args[i], $"/args/{i}");
        return found;
    }

    static string RefOf(JsonObject o, string field)
    {
        if (o.Count == 1 && o.TryGetValue("ref", out var r) && r is string reference && reference.Length > 0) return reference;
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: an object argument must be {{\"ref\": \"r1...\"}}, a reference to resolve; anything else is passed as a number, string, boolean, array or null",
            new JsonObject { ["field"] = field });
    }

    /// <summary>The arguments converted to what <paramref name="signature"/> takes (as they come when it is not known).</summary>
    public static object?[] Arguments(LateBinder binder, IReadOnlyList<object?> args, ApiSignature? signature, string interfaceName, IReadOnlyDictionary<string, object> resolved)
    {
        if (signature != null && args.Count != signature.Parameters.Count)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{interfaceName}.{signature} takes {signature.Parameters.Count} argument{(signature.Parameters.Count == 1 ? "" : "s")}, and {args.Count} {(args.Count == 1 ? "was" : "were")} given; pass a placeholder (0, null) for an out argument",
                new JsonObject { ["field"] = "args", ["signature"] = signature.ToString() });
        }
        var values = new object?[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            var p = signature?.Parameters[i];
            values[i] = Convert(binder, args[i], p, $"/args/{i}", signature, resolved);
        }
        return values;
    }

    static object? Convert(LateBinder binder, object? json, ApiParameter? p, string field, ApiSignature? signature, IReadOnlyDictionary<string, object> resolved)
    {
        var type = p == null || p.Type == typeof(object) ? null : p.Type;
        SwAgentException Bad(string why) => new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: {why}{(signature == null ? "" : $" ({p} in {signature})")}", new JsonObject { ["field"] = field });

        if (json is JsonObject o) return resolved[RefOf(o, field)];
        if (json is List<object?> list) return ArrayOf(binder, list, type, field, p, signature, resolved);
        if (json == null)
        {
            if (type == null || !type.IsValueType) return null;
            // A placeholder for a ref or out value (SOLIDWORKS' interop declares its returned values either way).
            if (p!.ByRef) return Activator.CreateInstance(type);
            throw Bad($"null cannot be passed as {ApiParameter.TypeName(type)}");
        }
        if (type == null) return json is long l ? (l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : (double)l) : json;
        if (type.IsArray) throw Bad($"needs an array of {ApiParameter.TypeName(type.GetElementType()!)}");
        if (type == typeof(string)) return json as string ?? throw Bad("needs a string");
        if (type == typeof(bool)) return json is bool b ? b : throw Bad("needs true or false");
        if (type == typeof(double) || type == typeof(float))
            return json is long || json is double ? System.Convert.ChangeType(json, type, CultureInfo.InvariantCulture) : throw Bad($"needs a number");
        if (type.IsEnum || type == typeof(int) || type == typeof(short) || type == typeof(long) || type == typeof(byte) || type == typeof(uint) || type == typeof(ushort))
        {
            long whole = json is long l2 ? l2 : json is double d && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : throw Bad("needs a whole number");
            try
            {
                return type.IsEnum ? Enum.ToObject(type, whole) : System.Convert.ChangeType(whole, type, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                throw Bad($"{whole} does not fit {ApiParameter.TypeName(type)}");
            }
        }
        throw Bad($"needs an object: pass {{\"ref\": \"r1...\"}}");
    }

    static object ArrayOf(LateBinder binder, List<object?> list, Type? type, string field, ApiParameter? p, ApiSignature? signature, IReadOnlyDictionary<string, object> resolved)
    {
        if (list.Count > 0 && list.All(x => x is JsonObject)) return binder.ObjectArray(list.Select((x, i) => resolved[RefOf((JsonObject)x!, $"{field}/{i}")]).ToArray());
        var element = type != null && type.IsArray ? type.GetElementType() : null;
        var elementParameter = element == null ? null : new ApiParameter(p!.Name, element, false, false);
        var items = list.Select((x, i) => Convert(binder, x, elementParameter, $"{field}/{i}", signature, resolved)).ToList();
        System.Type made = element ?? (list.Count == 0 ? typeof(object)
            : list.All(x => x is long || x is double) ? typeof(double)
            : list.All(x => x is string) ? typeof(string)
            : list.All(x => x is bool) ? typeof(bool)
            : typeof(object));
        var array = System.Array.CreateInstance(made, items.Count);
        for (int i = 0; i < items.Count; i++)
            array.SetValue(made == typeof(double) ? System.Convert.ToDouble(items[i], CultureInfo.InvariantCulture) : items[i], i);
        return array;
    }

    /// <summary>
    /// A value the API returned as JSON: scalars and arrays as they are (enums as their number; NaN and infinity as
    /// null), an object as <paramref name="describe"/> names it (its reference when it is an entity SOLIDWORKS can
    /// persist, else its interface only).
    /// </summary>
    public static object? ToJson(object? value, Func<object, object?> describe)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case char c: return c.ToString();
            case Enum e: return System.Convert.ToInt64(e, CultureInfo.InvariantCulture);
            case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)d;
            case float f: return float.IsNaN(f) || float.IsInfinity(f) ? null : (object)(double)f;
            case decimal m: return (double)m;
            case byte _:
            case sbyte _:
            case short _:
            case ushort _:
            case int _:
            case uint _:
            case long _:
                return System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong u: return (double)u;
            case DateTime t: return t.ToString("o", CultureInfo.InvariantCulture);
            case DispatchWrapper w: return ToJson(w.WrappedObject, describe);
            case Array a:
                var list = new List<object?>(a.Length);
                foreach (var item in a) list.Add(ToJson(item, describe));
                return list;
        }
        return describe(value);
    }
}
