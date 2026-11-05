using System;
using System.Linq;
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

    /// <summary>
    /// A request held up by a modal dialog: <paramref name="started"/> when it raised the dialog and waits on it (its
    /// outcome follows as operation.completed); not started when the dialog was already open and nothing may run under it.
    /// </summary>
    public static SwAgentError DialogOpen(LedgerEntry entry, DialogInfo dialog, bool started)
    {
        string buttons = dialog.Buttons.Count == 0 ? "none readable" : string.Join(", ", dialog.Buttons.Select(b => b.Text));
        string message = started
            ? $"{entry.Request.Method} is waiting on the SOLIDWORKS dialog \"{dialog.Title}\" (buttons: {buttons}); answer it with " +
              $"dialog.respond (dialog {dialog.Id}); the operation's outcome follows as operation.completed"
            : $"{entry.Request.Method} was not started: SOLIDWORKS is showing the dialog \"{dialog.Title}\" (buttons: {buttons}); " +
              $"answer or close it with dialog.respond (dialog {dialog.Id}), then retry";
        return new SwAgentError(ErrorCode.DialogOpen, message, new JsonObject
        {
            ["started"] = started,
            ["operation_running"] = started,
            ["method"] = entry.Request.Method,
            ["dialog"] = JsonCodec.ToJson(dialog),
        });
    }

    static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);
}
