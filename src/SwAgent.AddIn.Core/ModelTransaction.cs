using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// What one model-changing operation has done so far, so that a failure leaves the part as it found it (design 371,
/// Model changes): everything it made is recorded as it is made, with how to remove it, and every edit with how to put
/// the old value back. A rollback undoes them newest first, trying every step even when one fails. Undo (EditUndo2) is
/// never used: its depth and grouping are not ours to control. UI thread only, like the operation that owns it.
/// </summary>
public sealed class ModelTransaction
{
    readonly List<(string Label, Action Undo)> _steps = new List<(string, Action)>();

    public List<CreatedItem> Created { get; } = new List<CreatedItem>();

    public List<ChangedItem> Changed { get; } = new List<ChangedItem>();

    /// <summary>Records something the operation made, and how to remove it.</summary>
    public void Made(CreatedItem item, Action remove)
    {
        Created.Add(item);
        _steps.Add((item.Name, remove));
    }

    /// <summary>Records an edit, and how to put the old value back.</summary>
    public void Edited(ChangedItem item, Action restore)
    {
        Changed.Add(item);
        _steps.Add((item.Name, restore));
    }

    /// <summary>Undoes every recorded step, newest first, and forgets them.</summary>
    public RollbackOutcome Rollback()
    {
        var undone = new List<string>();
        var failed = new List<string>();
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            try
            {
                _steps[i].Undo();
                undone.Add(_steps[i].Label);
            }
            catch (Exception e)
            {
                failed.Add($"{_steps[i].Label}: {(e is SwAgentException s ? s.Error.Message : e.Message)}");
            }
        }
        _steps.Clear();
        Created.Clear();
        Changed.Clear();
        return new RollbackOutcome(undone, failed);
    }

    /// <summary>
    /// Rolls back and returns the error to answer with. A failure while changing keeps its own code (an unexpected
    /// exception is operation_failed); one while checking the change is verification_failed. Either way the details say
    /// what was rolled back. When the rollback itself fails, the answer is operation_failed with rollback_failed, what
    /// remains, and the code of what caused it; the document is left open with unsaved changes for the agent to inspect.
    /// A failure before anything was made is passed on as it is.
    /// </summary>
    public SwAgentException Abort(Exception cause, bool verifying)
    {
        if (_steps.Count == 0 && cause is SwAgentException unchanged) return unchanged;
        var error = cause is SwAgentException known
            ? known.Error
            : new SwAgentError(verifying ? ErrorCode.VerificationFailed : ErrorCode.OperationFailed,
                verifying ? $"the change was made but its check failed: {cause.Message}" : $"SOLIDWORKS failed during the change: {cause.Message}",
                new JsonObject { ["exception"] = cause.GetType().FullName });
        var outcome = Rollback();
        var details = new JsonObject();
        if (error.Details != null)
        {
            foreach (var pair in error.Details) details[pair.Key] = pair.Value;
        }
        details["rolled_back"] = outcome.Undone.Select(u => (object?)u).ToList();
        if (outcome.Clean)
        {
            string undone = outcome.Undone.Count == 0 ? "nothing had been made" : "what it made was rolled back: " + string.Join(", ", outcome.Undone);
            return new SwAgentException(new SwAgentError(error.Code, $"{error.Message}; {undone}", details));
        }
        details["rollback_failed"] = true;
        details["remaining"] = outcome.Failed.Select(f => (object?)f).ToList();
        details["cause"] = WireNames.ForEnum(error.Code);
        return new SwAgentException(new SwAgentError(ErrorCode.OperationFailed,
            $"{error.Message}; rolling it back failed too ({string.Join("; ", outcome.Failed)}), so the part is left with unsaved changes " +
            "for you to inspect (model.tree, model.rebuild_status); it was not closed or reverted",
            details));
    }
}

/// <summary>What a rollback undid, and each step it could not undo with why.</summary>
public sealed class RollbackOutcome
{
    public RollbackOutcome(IReadOnlyList<string> undone, IReadOnlyList<string> failed)
    {
        Undone = undone;
        Failed = failed;
    }

    public IReadOnlyList<string> Undone { get; }

    public IReadOnlyList<string> Failed { get; }

    public bool Clean => Failed.Count == 0;
}
