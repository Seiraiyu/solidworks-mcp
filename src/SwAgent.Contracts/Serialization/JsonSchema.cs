using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace SwAgent.Contracts.Serialization;

/// <summary>JSON Schema for contract types, from the same metadata the codec binds with.</summary>
public static class JsonSchema
{
    public static JsonObject For(Type type) => ForType(type, null);

    static JsonObject ForType(Type type, string? description)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        JsonObject schema;
        if (t == typeof(string)) schema = Typed("string");
        else if (t == typeof(bool)) schema = Typed("boolean");
        else if (t == typeof(int) || t == typeof(long)) schema = Typed("integer");
        else if (t == typeof(double)) schema = Typed("number");
        else if (t.IsEnum)
        {
            schema = Typed("string");
            schema["enum"] = WireNames.EnumNames(t).Select(n => (object?)n).ToList();
        }
        else if (t == typeof(JsonObject)) schema = Typed("object");
        else if (TypeMeta.IsAnyValue(t)) schema = new JsonObject();
        else if (TypeMeta.IsList(t, out var element))
        {
            schema = Typed("array");
            schema["items"] = ForType(element, null);
        }
        else
        {
            var meta = TypeMeta.For(t);
            schema = Typed("object");
            string? typeDescription = t.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (typeDescription != null && description == null) schema["description"] = typeDescription;
            var properties = new JsonObject();
            var required = new List<object?>();
            foreach (var p in meta.Properties)
            {
                properties[p.WireName] = ForType(p.Info.PropertyType, p.Description);
                if (p.Required) required.Add(p.WireName);
            }
            schema["properties"] = properties;
            if (required.Count > 0) schema["required"] = required;
            schema["additionalProperties"] = false;
        }
        if (description != null) schema["description"] = description;
        return schema;
    }

    static JsonObject Typed(string name) => new JsonObject { ["type"] = name };
}
