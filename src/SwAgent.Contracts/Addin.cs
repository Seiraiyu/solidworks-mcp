using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("addin.state", typeof(AddinStateResult), Thread = OperationThread.Pipe, Internal = true)]
[Description("The add-in's own state, answered even while SOLIDWORKS' UI thread is busy: what runs, what waits, documents, preferences.")]
public sealed class AddinStateRequest
{
}

[Description("The add-in's state.")]
public sealed class AddinStateResult
{
    [Required]
    [Description("The add-in's assembly version.")]
    public string AddinVersion { get; set; } = "";

    [Description("The protocol version the add-in speaks.")]
    public int ProtocolVersion { get; set; }

    [Description("The process id of sldworks.exe.")]
    public int ProcessId { get; set; }

    [Description("The request running on the UI thread, absent when idle.")]
    public RunningRequestInfo? Running { get; set; }

    [Required]
    [Description("Requests waiting for the UI thread, oldest first.")]
    public List<QueuedRequestInfo> Queued { get; set; } = new List<QueuedRequestInfo>();

    [Description("Milliseconds since the UI thread last ran the add-in's window procedure; absent before it ever has.")]
    public long? UiLastPumpedMs { get; set; }

    [Description("True while the watchdog thread is alive.")]
    public bool WatchdogAlive { get; set; }

    [Description("True while the automation preferences are applied for a connected client.")]
    public bool PreferencesApplied { get; set; }

    [Description("True when, at load, preferences left applied by an earlier crash were restored.")]
    public bool PreferencesRecovered { get; set; }

    [Required]
    [Description("Automation preferences SOLIDWORKS did not take (set failed or did not stick); empty when all took.")]
    public List<string> PreferencesNotApplied { get; set; } = new List<string>();

    [Required]
    [Description("Open documents and their dirty flags, as last read.")]
    public List<DocumentInfo> Documents { get; set; } = new List<DocumentInfo>();

    [Description("When the documents were last read, UTC, ISO 8601; absent before the first read.")]
    public string? DocumentsAsOf { get; set; }

    [Description("The sequence number of the last event raised, 0 before any.")]
    public long LastEventSeq { get; set; }
}

[Description("The request on the UI thread.")]
public sealed class RunningRequestInfo
{
    [Description("Its id.")]
    public long Id { get; set; }

    [Required]
    [Description("Its operation.")]
    public string Method { get; set; } = "";

    [Description("How long it has run.")]
    public long RunningMs { get; set; }

    [Description("True when its client was already answered (timeout) and its outcome will come as operation.completed.")]
    public bool Answered { get; set; }
}

[Description("A request waiting for the UI thread.")]
public sealed class QueuedRequestInfo
{
    [Description("Its id.")]
    public long Id { get; set; }

    [Required]
    [Description("Its operation.")]
    public string Method { get; set; } = "";

    [Description("How long it has waited.")]
    public long WaitedMs { get; set; }
}

[Operation("addin.events", typeof(AddinEventsResult), Thread = OperationThread.Pipe, Internal = true)]
[Description("Events the add-in still holds, after a sequence number, for a client that missed them.")]
public sealed class AddinEventsRequest
{
    [Description("Return events after this sequence number; all held events when absent.")]
    public long? AfterSeq { get; set; }
}

[Description("Held events.")]
public sealed class AddinEventsResult
{
    [Required]
    [Description("The events after after_seq, oldest first.")]
    public List<EventEnvelope> Events { get; set; } = new List<EventEnvelope>();

    [Description("True when events after after_seq were already dropped from the add-in's buffer.")]
    public bool Missed { get; set; }

    [Description("The sequence number of the last event raised.")]
    public long LastSeq { get; set; }
}
