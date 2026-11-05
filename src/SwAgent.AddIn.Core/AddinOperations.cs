using System;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>addin.state: answered on the pipe thread from the runtime, never from SOLIDWORKS.</summary>
public sealed class AddinStateOperation : PipeOperation<AddinStateRequest, AddinStateResult>
{
    readonly AgentRuntime _runtime;

    public AddinStateOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override AddinStateResult Run(AddinStateRequest request, IOperationContext context)
    {
        var now = _runtime.UtcNow();
        var snapshot = _runtime.Ledger.Snapshot();
        var running = snapshot.Running;
        var pumped = _runtime.LastPumpedUtc();
        var docs = _runtime.Documents;
        return new AddinStateResult
        {
            AddinVersion = _runtime.AddinVersion,
            ProtocolVersion = Protocol.Version,
            ProcessId = _runtime.ProcessId,
            Running = running == null ? null : new RunningRequestInfo
            {
                Id = running.Request.Id,
                Method = running.Request.Method,
                RunningMs = Ms(now - (running.StartedUtc ?? now)),
                Answered = running.IsAnswered,
            },
            Queued = snapshot.Queued.Select(e => new QueuedRequestInfo
            {
                Id = e.Request.Id,
                Method = e.Request.Method,
                WaitedMs = Ms(now - e.ReceivedUtc),
            }).ToList(),
            UiLastPumpedMs = pumped == null ? (long?)null : Ms(now - pumped.Value),
            WatchdogAlive = _runtime.WatchdogAlive(),
            PreferencesApplied = _runtime.Preferences?.Applied ?? false,
            PreferencesRecovered = _runtime.Preferences?.RecoveredAtLoad ?? false,
            PreferencesNotApplied = _runtime.Preferences?.NotTaken.ToList() ?? new System.Collections.Generic.List<string>(),
            Documents = docs?.Current.ToList() ?? new System.Collections.Generic.List<DocumentInfo>(),
            DocumentsAsOf = docs?.AsOfUtc?.ToString("o", CultureInfo.InvariantCulture),
            LastEventSeq = _runtime.Events.LastSeq,
        };
    }

    /// <summary>Read-only state: the check is that it describes this add-in.</summary>
    protected override void Verify(AddinStateRequest request, AddinStateResult result, IOperationContext context)
    {
        if (result.ProcessId != _runtime.ProcessId)
            throw new SwAgentException(ErrorCode.VerificationFailed, "addin.state described another process");
    }

    static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);
}

/// <summary>addin.events: the events the hub still holds, for a client that missed some.</summary>
public sealed class AddinEventsOperation : PipeOperation<AddinEventsRequest, AddinEventsResult>
{
    readonly AgentRuntime _runtime;

    public AddinEventsOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override AddinEventsResult Run(AddinEventsRequest request, IOperationContext context)
    {
        if (request.AfterSeq < 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "after_seq must not be negative");
        var (events, missed) = _runtime.Events.Since(request.AfterSeq);
        return new AddinEventsResult { Events = events.ToList(), Missed = missed, LastSeq = _runtime.Events.LastSeq };
    }

    protected override void Verify(AddinEventsRequest request, AddinEventsResult result, IOperationContext context)
    {
        if (result.Events.Any(e => e.Seq <= (request.AfterSeq ?? 0)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "addin.events returned an event at or before after_seq");
    }
}
