using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>Names an event kind whose data is this type. One type may carry several kinds; a kind has one type.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class EventAttribute : Attribute
{
    public EventAttribute(string kind) { Kind = kind; }
    public string Kind { get; }
}

public sealed class EventDescriptor
{
    public EventDescriptor(string kind, Type dataType, string description)
    {
        Kind = kind;
        DataType = dataType;
        Description = description;
    }

    public string Kind { get; }
    public Type DataType { get; }
    public string Description { get; }
}

/// <summary>Every event kind the contracts declare, validated once.</summary>
public static class EventCatalog
{
    static readonly Regex KindPattern = new Regex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant);

    static readonly Lazy<IReadOnlyList<EventDescriptor>> AllEvents = new Lazy<IReadOnlyList<EventDescriptor>>(() =>
        FromTypes(typeof(EventCatalog).Assembly.GetTypes().Where(t => t.GetCustomAttributes<EventAttribute>().Any())));

    public static IReadOnlyList<EventDescriptor> All => AllEvents.Value;

    public static EventDescriptor? Find(string kind) => All.FirstOrDefault(e => e.Kind == kind);

    public static IReadOnlyList<EventDescriptor> FromTypes(IEnumerable<Type> dataTypes)
    {
        var list = new List<EventDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in dataTypes)
        {
            TypeMeta.For(type);
            foreach (var a in type.GetCustomAttributes<EventAttribute>())
            {
                if (!KindPattern.IsMatch(a.Kind))
                    throw new ArgumentException($"event kind '{a.Kind}' on {type.Name} is not family.name in lower case");
                if (!seen.Add(a.Kind))
                    throw new ArgumentException($"two types claim event kind '{a.Kind}'");
                list.Add(new EventDescriptor(a.Kind, type, type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""));
            }
        }
        return list.OrderBy(e => e.Kind, StringComparer.Ordinal).ToList();
    }
}

/// <summary>One event as it travels: the params of an <c>event</c> notification.</summary>
[Description("An event the add-in raised.")]
public sealed class EventEnvelope
{
    /// <summary>The JSON-RPC method of an event notification.</summary>
    public const string Method = "event";

    [Description("Counts up from 1 for each add-in load; a gap means events were missed.")]
    public long Seq { get; set; }

    [Required]
    [Description("The event kind, for example document.saved.")]
    public string Kind { get; set; } = "";

    [Required]
    [Description("When it was raised, UTC, ISO 8601.")]
    public string Time { get; set; } = "";

    [Required]
    [Description("The kind's data.")]
    public JsonObject Data { get; set; } = new JsonObject();
}

[Event("document.opened")]
[Event("document.closed")]
[Event("document.saved")]
[Event("document.activated")]
[Description("A document an event is about.")]
public sealed class DocumentEvent
{
    [Required]
    [Description("The document's title as SOLIDWORKS shows it.")]
    public string Title { get; set; } = "";

    [Description("The full path, absent while the document has never been saved.")]
    public string? Path { get; set; }

    [Description("Part, assembly or drawing, when known.")]
    public DocumentType? Type { get; set; }
}

[Event("documents.changed")]
[Description("The open documents changed: one opened or closed, or a dirty flag changed.")]
public sealed class DocumentsChangedEvent
{
    [Required]
    [Description("Every open document, with its dirty flag.")]
    public List<DocumentInfo> Documents { get; set; } = new List<DocumentInfo>();
}

[Event("document.rebuilt")]
[Description("A rebuild finished.")]
public sealed class DocumentRebuiltEvent
{
    [Required]
    [Description("The document's title.")]
    public string Title { get; set; } = "";

    [Description("The full path, absent while unsaved.")]
    public string? Path { get; set; }

    [Description("Rebuild errors after the rebuild.")]
    public int Errors { get; set; }

    [Description("Rebuild warnings after the rebuild.")]
    public int Warnings { get; set; }
}

[Event("operation.completed")]
[Description("A request that had already been answered (timeout) finished; this is how it ended.")]
public sealed class OperationCompletedEvent
{
    [Description("The request id.")]
    public long Id { get; set; }

    [Required]
    [Description("The operation.")]
    public string Method { get; set; } = "";

    [Description("True when it succeeded.")]
    public bool Ok { get; set; }

    [Description("Its error, when it failed.")]
    public SwAgentError? Error { get; set; }

    [Description("How long it ran.")]
    public long DurationMs { get; set; }

    [Required]
    [Description("What the client had already been told, for example timeout.")]
    public string AnsweredAs { get; set; } = "";
}

[Event("session.closing")]
[Description("The add-in is unloading: SOLIDWORKS is exiting or the add-in was turned off.")]
public sealed class SessionClosingEvent
{
    [Required]
    [Description("Why.")]
    public string Reason { get; set; } = "";
}
