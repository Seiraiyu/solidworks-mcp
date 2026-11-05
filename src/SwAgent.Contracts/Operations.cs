using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>Where the add-in runs an operation.</summary>
public enum OperationThread
{
    /// <summary>Queued and run on SOLIDWORKS' UI thread, one at a time. Every operation that calls SOLIDWORKS.</summary>
    Ui,

    /// <summary>Answered at once on the pipe's reader thread, even while the UI thread is busy. Never calls SOLIDWORKS.</summary>
    Pipe,
}

/// <summary>Names the operation a request type starts and the type of its result. Nothing else names an operation.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class OperationAttribute : Attribute
{
    public OperationAttribute(string name, Type resultType)
    {
        Name = name;
        ResultType = resultType;
    }

    public string Name { get; }
    public Type ResultType { get; }

    /// <summary>Where the add-in runs it; the UI thread unless the operation only reads the add-in's own state.</summary>
    public OperationThread Thread { get; set; } = OperationThread.Ui;

    /// <summary>True for operations the client uses itself and never offers to an agent as an MCP tool.</summary>
    public bool Internal { get; set; }

    /// <summary>True for operations that never change a document or SOLIDWORKS' state; their MCP tools say so.</summary>
    public bool ReadOnly { get; set; }

    public string Family => Name.IndexOf('.') < 0 ? Name : Name.Substring(0, Name.IndexOf('.'));
}

public sealed class OperationDescriptor
{
    public OperationDescriptor(string name, string family, Type requestType, Type resultType, string description,
        OperationThread thread = OperationThread.Ui, bool isInternal = false, bool readOnly = false)
    {
        Name = name;
        Family = family;
        RequestType = requestType;
        ResultType = resultType;
        Description = description;
        Thread = thread;
        Internal = isInternal;
        ReadOnly = readOnly;
    }

    public string Name { get; }
    public string Family { get; }
    public Type RequestType { get; }
    public Type ResultType { get; }
    public string Description { get; }
    public OperationThread Thread { get; }
    public bool Internal { get; }
    public bool ReadOnly { get; }

    /// <summary>The MCP tool name of an operation: sw_ and the name with dots as underscores (dialog.respond is sw_dialog_respond).</summary>
    public string ToolName => "sw_" + Name.Replace('.', '_');
}

/// <summary>Every operation the contracts declare, validated once.</summary>
public static class OperationCatalog
{
    static readonly Regex NamePattern = new Regex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant);

    static readonly Lazy<IReadOnlyList<OperationDescriptor>> AllOperations =
        new Lazy<IReadOnlyList<OperationDescriptor>>(() => FromAssembly(typeof(OperationCatalog).Assembly));

    public static IReadOnlyList<OperationDescriptor> All => AllOperations.Value;

    public static OperationDescriptor? Find(string name) => All.FirstOrDefault(o => o.Name == name);

    public static IReadOnlyList<OperationDescriptor> FromAssembly(Assembly assembly) =>
        FromTypes(assembly.GetTypes().Where(t => t.GetCustomAttribute<OperationAttribute>() != null));

    public static IReadOnlyList<OperationDescriptor> FromTypes(IEnumerable<Type> requestTypes)
    {
        var list = new List<OperationDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in requestTypes)
        {
            var a = type.GetCustomAttribute<OperationAttribute>()
                ?? throw new ArgumentException($"{type.FullName} has no [Operation] attribute");
            if (!NamePattern.IsMatch(a.Name))
                throw new ArgumentException($"operation name '{a.Name}' on {type.Name} is not family.verb in lower case");
            if (!seen.Add(a.Name))
                throw new ArgumentException($"two request types claim operation '{a.Name}'");
            TypeMeta.For(type);
            TypeMeta.For(a.ResultType);
            list.Add(new OperationDescriptor(a.Name, a.Family, type, a.ResultType,
                type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "", a.Thread, a.Internal, a.ReadOnly));
        }
        return list.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }
}
