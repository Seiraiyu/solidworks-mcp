using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("diag.sleep", typeof(DiagSleepResult), Internal = true)]
[Description("Fault testing only: holds SOLIDWORKS' UI thread for a time without pumping messages. Refused unless diagnostics are on.")]
public sealed class DiagSleepRequest
{
    [Description("How long to hold the UI thread, 1 to 600000 ms.")]
    public int Ms { get; set; }
}

[Description("How long the UI thread was held.")]
public sealed class DiagSleepResult
{
    [Description("Measured milliseconds.")]
    public long SleptMs { get; set; }
}

/// <summary>The buttons diag.dialog shows.</summary>
public enum DiagDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

[Operation("diag.dialog", typeof(DiagDialogResult), Internal = true)]
[Description("Fault testing only: shows a modal message box owned by SOLIDWORKS' main window and waits until it is answered. Refused unless diagnostics are on.")]
public sealed class DiagDialogRequest
{
    [Required]
    [Description("The message box title.")]
    public string Title { get; set; } = "";

    [Required]
    [Description("The message box text.")]
    public string Text { get; set; } = "";

    [Description("Which buttons it shows: ok, ok_cancel or yes_no.")]
    public DiagDialogButtons Buttons { get; set; }
}

[Description("How the message box was answered.")]
public sealed class DiagDialogResult
{
    [Required]
    [Description("The button that closed it: ok, cancel, yes or no.")]
    public string Pressed { get; set; } = "";
}

[Operation("diag.scratch_part", typeof(DocumentInfo), Internal = true)]
[Description("Fault testing only: creates a part from the default part template, saves it as a new file in the live suite's scratch directory, and marks it as having unsaved changes. Refused unless diagnostics are on.")]
public sealed class DiagScratchPartRequest
{
    [Required]
    [Description("The new part's full path: a .SLDPRT file under %LOCALAPPDATA%/solidworks-mcp/live-scratch that does not exist yet.")]
    public string Path { get; set; } = "";
}

[Operation("diag.scratch_block", typeof(DocumentInfo), Internal = true)]
[Description("Live testing only: creates a part from the default part template holding one block, x by y by z mm, from the origin along +X, +Y and +Z (a rectangle on the first plane extruded along its normal), and saves it as a new file in the live suite's scratch directory. Refused unless diagnostics are on.")]
public sealed class DiagScratchBlockRequest
{
    [Required]
    [Description("The new part's full path: a .SLDPRT file under %LOCALAPPDATA%/solidworks-mcp/live-scratch that does not exist yet.")]
    public string Path { get; set; } = "";

    [Description("Size along X, mm, more than 0 and at most 1000.")]
    public double XMm { get; set; }

    [Description("Size along Y, mm, more than 0 and at most 1000.")]
    public double YMm { get; set; }

    [Description("Size along Z, mm, more than 0 and at most 1000.")]
    public double ZMm { get; set; }
}

[Operation("diag.editing", typeof(DiagEditingResult), Internal = true, ReadOnly = true)]
[Description("Fault testing only: what the user has open that makes model changes wait, read exactly as their precondition reads it (the sketch being edited in the part, the command whose PropertyManager is open). Changes nothing. Refused unless diagnostics are on.")]
public sealed class DiagEditingRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("What the user has open.")]
public sealed class DiagEditingResult
{
    [Description("The sketch the user is editing in the part; absent when none.")]
    public string? Sketch { get; set; }

    [Description("The title of the command (PropertyManager) the user has open; absent when none.")]
    public string? Command { get; set; }
}
[Operation("diag.edit_sketch", typeof(DiagEditingResult), Internal = true)]
[Description("Fault testing only: opens a sketch for editing as the user would (Edit Sketch) and leaves it open (open true), or exits the sketch being edited (open false), so the fault suite can show a model change waiting for the user without anyone at SOLIDWORKS. Refused unless diagnostics are on.")]
public sealed class DiagEditSketchRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The sketch to open, by name or reference; for open true.")]
    public string? Sketch { get; set; }

    [Description("true to open the sketch for editing, false to exit the sketch being edited.")]
    public bool Open { get; set; }
}
