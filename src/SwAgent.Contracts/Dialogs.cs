using System.Collections.Generic;
using System.ComponentModel;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts;

[Description("A push button on a dialog.")]
public sealed class DialogButton
{
    [Required]
    [Description("The button's text, without its & accelerator marks.")]
    public string Text { get; set; } = "";

    [Description("The button's control id; dialog.respond accepts it in place of the text.")]
    public int ControlId { get; set; }
}

[Description("A modal dialog SOLIDWORKS is showing.")]
public sealed class DialogInfo
{
    [Required]
    [Description("Names this dialog until it closes; pass it to dialog.respond.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("The dialog's title bar text.")]
    public string Title { get; set; } = "";

    [Required]
    [Description("The dialog's text, one entry per static text control; empty when it could not be read.")]
    public List<string> Text { get; set; } = new List<string>();

    [Required]
    [Description("Its push buttons; empty when they could not be read (close still works).")]
    public List<DialogButton> Buttons { get; set; } = new List<DialogButton>();

    [Description("The request that was running when it opened; absent when none was.")]
    public long? RequestId { get; set; }

    [Required]
    [Description("A 3DEXPERIENCE sign-in or licence window: only a person answers it, and dialog.respond refuses to. Ask the user to deal with it.")]
    public bool NeedsPerson { get; set; }

    [Required]
    [Description("When it was first seen, UTC, ISO 8601.")]
    public string OpenedAt { get; set; } = "";
}

/// <summary>Who pressed a dialog's button.</summary>
public enum DialogAnsweredBy
{
    Agent,
    AllowList,
}

[Event("dialog.opened")]
[Event("dialog.closed")]
[Description("A modal dialog opened or closed.")]
public sealed class DialogEvent
{
    [Required]
    [Description("The dialog.")]
    public DialogInfo Dialog { get; set; } = new DialogInfo();
}

[Event("dialog.answered")]
[Description("A button was pressed on a dialog, or it was closed.")]
public sealed class DialogAnsweredEvent
{
    [Required]
    [Description("The dialog id.")]
    public string Dialog { get; set; } = "";

    [Required]
    [Description("The dialog's title.")]
    public string Title { get; set; } = "";

    [Required]
    [Description("The button pressed, or close.")]
    public string Button { get; set; } = "";

    [Description("Who pressed it: the agent, or the add-in's allow-list of known prompts.")]
    public DialogAnsweredBy By { get; set; }
}

[Operation("dialog.respond", typeof(DialogRespondResult), Thread = OperationThread.Pipe)]
[Description("Presses a button on the modal dialog SOLIDWORKS is showing, or closes it. Answered even while the dialog holds SOLIDWORKS' UI thread. When the dialog belonged to a running request, waits for that request's outcome.")]
public sealed class DialogRespondRequest
{
    [Required]
    [Description("The dialog id, from a dialog_open error or addin.state.")]
    public string Dialog { get; set; } = "";

    [Required]
    [Description("A button's text (case and & ignored), its control id, or close.")]
    public string Button { get; set; } = "";

    [Description("How long to wait for the outcome of the request the dialog belonged to, 0 to 60000 ms; 5000 when absent.")]
    public int? WaitMs { get; set; }
}

/// <summary>What followed a dialog.respond.</summary>
public enum DialogOutcome
{
    /// <summary>The dialog belonged to no request.</summary>
    NoRequest,

    /// <summary>The request it belonged to finished and succeeded.</summary>
    Result,

    /// <summary>The request it belonged to finished and failed.</summary>
    Error,

    /// <summary>The request raised another dialog.</summary>
    DialogOpen,

    /// <summary>The request was still running when the wait ended; its outcome follows as operation.completed.</summary>
    StillRunning,
}

[Description("What was pressed, and what followed.")]
public sealed class DialogRespondResult
{
    [Required]
    [Description("The dialog id.")]
    public string Dialog { get; set; } = "";

    [Required]
    [Description("The button pressed (its text), or close.")]
    public string Pressed { get; set; } = "";

    [Description("The control id pressed; absent for close.")]
    public int? ControlId { get; set; }

    [Description("What followed: no_request, result, error, dialog_open or still_running.")]
    public DialogOutcome Outcome { get; set; }

    [Description("The request's result, when the outcome is result.")]
    public JsonObject? Result { get; set; }

    [Description("The request's error, when the outcome is error.")]
    public SwAgentError? Error { get; set; }

    [Description("The next dialog the request raised, when the outcome is dialog_open.")]
    public DialogInfo? NextDialog { get; set; }
}
