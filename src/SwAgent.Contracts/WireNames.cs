using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SwAgent.Contracts;

/// <summary>How names appear on the wire: snake_case unless a <see cref="WireNameAttribute"/> says otherwise.</summary>
public static class WireNames
{
    static readonly ConcurrentDictionary<Type, EnumMap> Enums = new ConcurrentDictionary<Type, EnumMap>();

    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool afterLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool endsAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLowerOrDigit || endsAcronym) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static string ForProperty(PropertyInfo property) =>
        property.GetCustomAttribute<WireNameAttribute>()?.Name ?? SnakeCase(property.Name);

    public static string ForEnum(Enum value)
    {
        var map = MapFor(value.GetType());
        if (map.ToWire.TryGetValue(value, out var name)) return name;
        throw new ArgumentException($"{value.GetType().Name} has no member with value {Convert.ToInt64(value)}");
    }

    public static IReadOnlyList<string> EnumNames(Type enumType) => MapFor(enumType).Names;

    public static bool TryParseEnum(Type enumType, string wire, out object? value)
    {
        if (MapFor(enumType).FromWire.TryGetValue(wire, out var found))
        {
            value = found;
            return true;
        }
        value = null;
        return false;
    }

    static EnumMap MapFor(Type type) => Enums.GetOrAdd(type, BuildMap);

    static EnumMap BuildMap(Type type)
    {
        if (!type.IsEnum) throw new ArgumentException($"{type.Name} is not an enum");
        var map = new EnumMap();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => f.MetadataToken))
        {
            string wire = field.GetCustomAttribute<WireNameAttribute>()?.Name ?? SnakeCase(field.Name);
            var value = (Enum)field.GetValue(null)!;
            map.ToWire[value] = wire;
            map.FromWire[wire] = value;
            map.Names.Add(wire);
        }
        return map;
    }

    sealed class EnumMap
    {
        public readonly Dictionary<Enum, string> ToWire = new Dictionary<Enum, string>();
        public readonly Dictionary<string, Enum> FromWire = new Dictionary<string, Enum>(StringComparer.Ordinal);
        public readonly List<string> Names = new List<string>();
    }
}
