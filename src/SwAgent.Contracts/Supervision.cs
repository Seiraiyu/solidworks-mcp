using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

/// <summary>
/// Names a tool the MCP server answers itself, from the supervisor, rather than by forwarding an add-in operation.
/// Its request type is the tool's input; deadline_ms is added by the server to every tool, as for operations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ServerToolAttribute : Attribute
{
    public ServerToolAttribute(string name, Type resultType)
    {
        Name = name;
        ResultType = resultType;
    }

    public string Name { get; }
    public Type ResultType { get; }
}

public sealed class ServerToolDescriptor
{
    public ServerToolDescriptor(string name, Type requestType, Type resultType, string description)
    {
        Name = name;
        RequestType = requestType;
        ResultType = resultType;
        Description = description;
    }

    public string Name { get; }
    public Type RequestType { get; }
    public Type ResultType { get; }
    public string Description { get; }
}

/// <summary>Every server tool the contracts declare, validated once.</summary>
public static class ServerToolCatalog
{
    static readonly Regex NamePattern = new Regex("^sw_[a-z][a-z_]*$", RegexOptions.CultureInvariant);

    static readonly Lazy<IReadOnlyList<ServerToolDescriptor>> AllTools = new Lazy<IReadOnlyList<ServerToolDescriptor>>(() =>
        FromTypes(typeof(ServerToolCatalog).Assembly.GetTypes().Where(t => t.GetCustomAttribute<ServerToolAttribute>() != null)));

    public static IReadOnlyList<ServerToolDescriptor> All => AllTools.Value;

    public static ServerToolDescriptor? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    public static IReadOnlyList<ServerToolDescriptor> FromTypes(IEnumerable<Type> requestTypes)
    {
        var list = new List<ServerToolDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in requestTypes)
        {
            var a = type.GetCustomAttribute<ServerToolAttribute>()
                ?? throw new ArgumentException($"{type.FullName} has no [ServerTool] attribute");
            if (!NamePattern.IsMatch(a.Name))
                throw new ArgumentException($"server tool name '{a.Name}' on {type.Name} is not sw_ and lower-case words");
            if (!seen.Add(a.Name))
                throw new ArgumentException($"two request types claim server tool '{a.Name}'");
            TypeMeta.For(type);
            TypeMeta.For(a.ResultType);
            list.Add(new ServerToolDescriptor(a.Name, type, a.ResultType, type.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""));
        }
        return list.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>What the supervisor found when it last looked for SOLIDWORKS.</summary>
public enum SupervisorState
{
    /// <summary>No sldworks.exe in this Windows session.</summary>
    NotRunning,

    /// <summary>More than one SOLIDWORKS, and none named.</summary>
    Multiple,

    /// <summary>The process exists but has not finished starting (or waits at a sign-in or update prompt).</summary>
    Starting,

    /// <summary>Ready, add-in not serving yet; LoadAddIn in progress.</summary>
    Loading,

    /// <summary>Connected to the add-in, hello exchanged.</summary>
    Ready,

    /// <summary>The connected SOLIDWORKS died; the next look rediscovers.</summary>
    Crashed,

    /// <summary>SOLIDWORKS is ready but the add-in could not be reached or loaded; error says why.</summary>
    AddinUnavailable,
}

[ServerTool("sw_status", typeof(SwStatusResult))]
[Description("Says what SOLIDWORKS is doing: whether it is running, what the add-in is running and has queued, any open dialog, the open documents and, when SOLIDWORKS is idle, the active document. Always answers, even while SOLIDWORKS is busy, stopped in a dialog, starting or absent.")]
public sealed class SwStatusRequest
{
}

[Description("The state of SOLIDWORKS as the server sees it.")]
public sealed class SwStatusResult
{
    [Description("not_running, multiple, starting, loading, ready, crashed or addin_unavailable.")]
    public SupervisorState State { get; set; }

    [Required]
    [Description("What the state means and what to do next.")]
    public string Message { get; set; } = "";

    [Required]
    [Description("This swmcp's version.")]
    public string SwmcpVersion { get; set; } = "";

    [Description("The SOLIDWORKS process the server talks to, or would; absent when there is none or several.")]
    public int? ProcessId { get; set; }

    [Required]
    [Description("Every SOLIDWORKS process in this Windows session.")]
    public List<int> ProcessIds { get; set; } = new List<int>();

    [Description("False when SOLIDWORKS' UI thread has not answered for over 5 s while nothing was running, or Windows reports its window hung; absent when not connected.")]
    public bool? Responding { get; set; }

    [Description("How long SOLIDWORKS has not responded, when responding is false.")]
    public long? NotRespondingMs { get; set; }

    [Description("The add-in's own state; absent when not connected.")]
    public AddinStateResult? Addin { get; set; }

    [Description("The session status (active document); present only when SOLIDWORKS' UI thread was idle.")]
    public SessionStatusResult? Session { get; set; }

    [Description("Why the server could not connect, when it could not.")]
    public SwAgentError? Error { get; set; }

    [Description("The last crash since the server started (or since sw_reconnect).")]
    public CrashInfo? LastCrash { get; set; }
}

[Description("A SOLIDWORKS crash as the server saw it.")]
public sealed class CrashInfo
{
    [Description("The process that died.")]
    public int ProcessId { get; set; }

    [Description("Its exit code, when Windows reported one.")]
    public int? ExitCode { get; set; }

    [Required]
    [Description("When the crash was seen, UTC, ISO 8601.")]
    public string At { get; set; } = "";

    [Required]
    [Description("The calls that were in flight.")]
    public List<InFlightRequest> InFlight { get; set; } = new List<InFlightRequest>();

    [Required]
    [Description("Documents that had unsaved changes in the last document table the add-in sent.")]
    public List<DocumentInfo> DirtyDocuments { get; set; } = new List<DocumentInfo>();

    [Description("When that document table was sent, UTC, ISO 8601; absent when none was.")]
    public string? DocumentsAsOf { get; set; }
}

[Description("A call in flight when SOLIDWORKS died.")]
public sealed class InFlightRequest
{
    [Required]
    [Description("Its operation.")]
    public string Method { get; set; } = "";

    [Description("How long it had been running.")]
    public long RunningMs { get; set; }
}

[ServerTool("sw_events", typeof(SwEventsResult))]
[Description("Events the server has received from SOLIDWORKS, oldest first, after a cursor: documents opened, saved, closed and changed, dialogs, and the outcomes of operations that were already answered timeout or dialog_open.")]
public sealed class SwEventsRequest
{
    [Description("Return events after this sequence number (the last_seq of an earlier answer); every kept event when absent.")]
    public long? AfterSeq { get; set; }

    [Description("Only these kinds, for example document.saved; every kind when absent.")]
    public List<string>? Kinds { get; set; }

    [Description("At most this many events, 1 to 1000; 100 when absent.")]
    public int? Limit { get; set; }
}

[Description("Events, oldest first.")]
public sealed class SwEventsResult
{
    [Required]
    [Description("The events after after_seq.")]
    public List<EventEnvelope> Events { get; set; } = new List<EventEnvelope>();

    [Description("True when events after after_seq were lost: the add-in's sequence skipped, or the server dropped old ones.")]
    public bool Missed { get; set; }

    [Description("Pass this as after_seq next time; the sequence of the last event returned, or after_seq when none was.")]
    public long LastSeq { get; set; }

    [Description("True when more events matched than limit allowed.")]
    public bool More { get; set; }

    [Description("The SOLIDWORKS process the events came from; absent before any connection.")]
    public int? ProcessId { get; set; }
}

[ServerTool("sw_reconnect", typeof(SwStatusResult))]
[Description("Drops the connection to SOLIDWORKS, forgets an add-in load that failed, and connects to the named SOLIDWORKS (or the only one), loading the add-in if needed. Answers with what sw_status would.")]
public sealed class SwReconnectRequest
{
    [Description("The SOLIDWORKS process to use, from sw_status' process_ids; the only one when absent.")]
    public int? ProcessId { get; set; }
}
