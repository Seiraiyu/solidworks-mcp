using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace SwAgent.Contracts.Serialization;

/// <summary>A contract property as the codec and schema generator see it.</summary>
internal sealed class PropMeta
{
    public PropMeta(PropertyInfo info, string wireName, bool required, string? description)
    {
        Info = info;
        WireName = wireName;
        Required = required;
        Description = description;
    }

    public PropertyInfo Info { get; }
    public string WireName { get; }
    public bool Required { get; }
    public string? Description { get; }
}

/// <summary>Validated, cached metadata for one contract class.</summary>
internal sealed class TypeMeta
{
    static readonly ConcurrentDictionary<Type, TypeMeta> Cache = new ConcurrentDictionary<Type, TypeMeta>();

    [ThreadStatic] static HashSet<Type>? _building;

    readonly ConstructorInfo _constructor;

    TypeMeta(Type type, ConstructorInfo constructor, List<PropMeta> properties)
    {
        Type = type;
        _constructor = constructor;
        Properties = properties;
        ByWireName = properties.ToDictionary(p => p.WireName, StringComparer.Ordinal);
    }

    public Type Type { get; }
    public IReadOnlyList<PropMeta> Properties { get; }
    public IReadOnlyDictionary<string, PropMeta> ByWireName { get; }

    public object Create() => _constructor.Invoke(null);

    public static TypeMeta For(Type type) => Cache.GetOrAdd(type, Build);

    internal static bool IsScalar(Type t) =>
        t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(double) || t.IsEnum;

    internal static bool IsList(Type t, out Type element)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            element = t.GetGenericArguments()[0];
            return true;
        }
        element = typeof(object);
        return false;
    }

    internal static bool IsContractClass(Type t) =>
        t.IsClass && !t.IsAbstract && t != typeof(string) && t != typeof(JsonObject) && !IsList(t, out _);

    static TypeMeta Build(Type type)
    {
        if (!IsContractClass(type)) throw new NotSupportedException($"{type.FullName} is not a contract class");
        var constructor = type.GetConstructor(Type.EmptyTypes)
            ?? throw new NotSupportedException($"{type.FullName} needs a public parameterless constructor to be a contract type");
        var building = _building ??= new HashSet<Type>();
        if (!building.Add(type)) throw new NotSupportedException($"{type.FullName} refers to itself; contract types must be trees");
        try
        {
            var properties = new List<PropMeta>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.MetadataToken))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.GetMethod == null || p.SetMethod == null || !p.SetMethod.IsPublic)
                    throw new NotSupportedException($"{type.Name}.{p.Name} needs a public getter and setter");
                EnsureSupported(p.PropertyType, $"{type.Name}.{p.Name}");
                bool required = p.GetCustomAttribute<RequiredAttribute>() != null
                    || (p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) == null);
                properties.Add(new PropMeta(p, WireNames.ForProperty(p), required, p.GetCustomAttribute<DescriptionAttribute>()?.Description));
            }
            var duplicate = properties.GroupBy(p => p.WireName).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new NotSupportedException($"{type.Name} has two properties named '{duplicate.Key}' on the wire");
            return new TypeMeta(type, constructor, properties);
        }
        finally
        {
            building.Remove(type);
        }
    }

    static void EnsureSupported(Type t, string where)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (IsScalar(u) || u == typeof(JsonObject)) return;
        if (IsList(u, out var element)) { EnsureSupported(element, where + "[]"); return; }
        if (IsContractClass(u))
        {
            if (_building != null && _building.Contains(u))
                throw new NotSupportedException($"{where} refers back to {u.Name}; contract types must be trees");
            For(u);
            return;
        }
        throw new NotSupportedException(
            $"{where}: {t.Name} is not a supported contract type (use string, bool, int, long, double, an enum, List<T>, JsonObject or a contract class)");
    }
}
