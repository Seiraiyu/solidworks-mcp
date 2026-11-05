using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>The busy and timeout errors, built the same way by the dispatcher and the watchdog.</summary>
public static class DeadlineErrors
{
    /// <summary>
    /// A request never started because its deadline passed in the queue. <paramref name="running"/> is what holds the
    /// UI thread now; when nothing does (the dispatcher noticed it just after the previous request finished),
    /// <paramref name="ranBefore"/> is that previous request.
    /// </summary>
    public static SwAgentError Busy(LedgerEntry entry, LedgerEntry? running, LedgerEntry? ranBefore, int queuedAhead, DateTime nowUtc)
    {
        var blocker = running ?? ranBefore;
        long waited = Ms(nowUtc - entry.ReceivedUtc);
        var details = new JsonObject
        {
            ["started"] = false,
            ["waited_ms"] = waited,
            ["queued_ahead"] = (long)queuedAhead,
            ["running"] = blocker == null ? null : new JsonObject
            {
                ["id"] = blocker.Request.Id,
                ["method"] = blocker.Request.Method,
                ["running_ms"] = Ms((blocker.FinishedUtc ?? nowUtc) - (blocker.StartedUtc ?? nowUtc)),
                ["finished"] = blocker.FinishedUtc != null,
            },
        };
        string message = blocker == null
            ? $"{entry.Request.Method} was not started: SOLIDWORKS' UI thread did not take it up within its {entry.Request.DeadlineMs} ms deadline " +
              "(SOLIDWORKS may be showing a dialog or not responding); it is safe to retry; check sw_status first"
            : $"{entry.Request.Method} was not started: SOLIDWORKS was busy with {blocker.Request.Method} " +
              $"({Ms((blocker.FinishedUtc ?? nowUtc) - (blocker.StartedUtc ?? nowUtc))} ms) when its {entry.Request.DeadlineMs} ms deadline passed; " +
              "it is safe to retry, with a longer deadline";
        return new SwAgentError(ErrorCode.Busy, message, details);
    }

    /// <summary>A request that started in time but is still running past its deadline.</summary>
    public static SwAgentError Timeout(LedgerEntry running, DateTime nowUtc) =>
        new SwAgentError(ErrorCode.Timeout,
            $"{running.Request.Method} started but did not finish within its {running.Request.DeadlineMs} ms deadline; SOLIDWORKS is still running it and " +
            "cannot be interrupted, so check sw_status and wait for its operation.completed event before retrying",
            new JsonObject
            {
                ["started"] = true,
                ["method"] = running.Request.Method,
                ["deadline_ms"] = running.Request.DeadlineMs,
                ["running_ms"] = Ms(nowUtc - (running.StartedUtc ?? nowUtc)),
            });

    static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);
}
