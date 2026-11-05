using System;
using System.Collections;
using System.Collections.Generic;

namespace SwAgent.Contracts.Serialization;

/// <summary>
/// Converts contract objects to and from JSON trees. Both the add-in and the MCP server use this one codec,
/// so they cannot disagree about a type. Reading is strict: unknown properties, wrong types and missing required
/// values are <see cref="ErrorCode.InvalidArgument"/> naming the field.
/// </summary>
public static class JsonCodec
{
    public static string Serialize(object? value, bool indented = false) => Json.Write(ToJson(value), indented);

    public static T Deserialize<T>(string json) => (T)Deserialize(json, typeof(T))!;

    public static object? Deserialize(string json, Type type)
    {
        object? tree;
        try
        {
            tree = Json.Parse(json);
        }
        catch (JsonParseException e)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, "malformed JSON: " + e.Message,
                new JsonObject { ["position"] = (long)e.Position });
        }
        return FromJson(tree, type);
    }

    public static T FromJson<T>(object? json) => (T)FromJson(json, typeof(T))!;

    public static object? FromJson(object? json, Type type) => Read(json, type, "");

    public static object? ToJson(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case int i: return (long)i;
            case long l: return l;
            case double d: return d;
            case JsonObject o: return o;
            case Enum e: return WireNames.ForEnum(e);
            case IList list:
                var array = new List<object?>(list.Count);
                foreach (var item in list) array.Add(ToJson(item));
                return array;
        }
        var meta = TypeMeta.For(value.GetType());
        var obj = new JsonObject();
        foreach (var p in meta.Properties)
        {
            var v = p.Info.GetValue(value);
            if (v != null) obj.Add(p.WireName, ToJson(v));
        }
        return obj;
    }

    static object? Read(object? json, Type type, string path)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return json == null ? null : Read(json, underlying, path);
        if (json == null)
        {
            if (type.IsValueType) throw Fail(path, "must not be null");
            return null;
        }
        if (type == typeof(string)) return json as string ?? throw Fail(path, "must be a string");
        if (type == typeof(bool)) return json is bool b ? b : throw Fail(path, "must be true or false");
        if (type == typeof(long)) return json is long l ? l : throw Fail(path, "must be an integer");
        if (type == typeof(int))
        {
            if (json is long l && l >= int.MinValue && l <= int.MaxValue) return (int)l;
            throw Fail(path, $"must be an integer from {int.MinValue} to {int.MaxValue}");
        }
        if (type == typeof(double))
        {
            if (json is long l) return (double)l;
            if (json is double d) return d;
            throw Fail(path, "must be a number");
        }
        if (type == typeof(JsonObject)) return json as JsonObject ?? throw Fail(path, "must be an object");
        if (type.IsEnum)
        {
            if (json is string s && WireNames.TryParseEnum(type, s, out var parsed)) return parsed;
            throw Fail(path, "must be one of " + string.Join(", ", WireNames.EnumNames(type)));
        }
        if (TypeMeta.IsList(type, out var element))
        {
            if (!(json is List<object?> array)) throw Fail(path, "must be an array");
            var list = (IList)Activator.CreateInstance(type)!;
            for (int i = 0; i < array.Count; i++) list.Add(Read(array[i], element, $"{path}[{i}]"));
            return list;
        }
        if (!(json is JsonObject obj)) throw Fail(path, "must be an object");
        var meta = TypeMeta.For(type);
        foreach (var key in obj.Keys)
        {
            if (!meta.ByWireName.ContainsKey(key)) throw Fail(Join(path, key), $"is not a known property of {type.Name}");
        }
        var instance = meta.Create();
        foreach (var p in meta.Properties)
        {
            string at = Join(path, p.WireName);
            object? value = obj.TryGetValue(p.WireName, out var raw) ? Read(raw, p.Info.PropertyType, at) : null;
            if (value == null)
            {
                if (p.Required) throw Fail(at, "is required");
                continue;
            }
            p.Info.SetValue(instance, value);
        }
        return instance;
    }

    static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

    static SwAgentException Fail(string path, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument,
            (path.Length == 0 ? "the value" : $"'{path}'") + " " + message,
            new JsonObject { ["field"] = path });
}
