using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The guard around a read of what a drawing view shows of one component (IView.GetVisibleEntities2 with a component).
/// SOLIDWORKS 2026 SP4 opened a view's PropertyManager on such a read and left it open (5.D live run, round 4: Drawing
/// View8), which leaves SOLIDWORKS busy with nothing said. So every per-component sweep reads which command is running
/// (ISldWorks.GetRunningCommandInfo) before and after: one that was not running before and is after is cleared
/// (IModelDoc2.ClearSelection2) and the read refused with operation_failed, saying what SOLIDWORKS was left in and
/// whether it is still open. A command already running before the read is the user's, not the read's, and is left alone
/// (the busy check before an operation answers for that). A read that throws is checked the same way before its error
/// goes on, so no path leaves the command open unannounced.
/// </summary>
public static class ViewSweepGuard
{
    /// <summary>
    /// Runs <paramref name="read"/>, a read of <paramref name="view"/> described by <paramref name="what"/> (for the
    /// refusal), with <paramref name="running"/> giving the running command's title (null for none) and
    /// <paramref name="clear"/> clearing the selection.
    /// </summary>
    public static T Read<T>(Func<string?> running, Action clear, string view, string what, Func<T> read)
    {
        string? before = running();
        T result;
        try
        {
            result = read();
        }
        catch
        {
            Check(running, clear, before, view, what);
            throw;
        }
        Check(running, clear, before, view, what);
        return result;
    }

    static void Check(Func<string?> running, Action clear, string? before, string view, string what)
    {
        if (before != null || !(running() is string left)) return;
        clear();
        throw Refusal(view, what, left, running());
    }

    /// <summary>The refusal for a read of <paramref name="view"/> that left SOLIDWORKS in <paramref name="left"/>; <paramref name="still"/> is what is open after clearing it (null: cleared).</summary>
    public static SwAgentException Refusal(string view, string what, string left, string? still) =>
        new SwAgentException(ErrorCode.OperationFailed,
            $"{what} in {view} (IView.GetVisibleEntities2 per component) left SOLIDWORKS in '{left}'"
            + (still == null ? "; it was cleared. " : $"; '{still}' is still open: close it in SOLIDWORKS. ")
            + "This way of reading an assembly view is not usable here",
            new JsonObject { ["view"] = view, ["command"] = left, ["cleared"] = still == null });
}
