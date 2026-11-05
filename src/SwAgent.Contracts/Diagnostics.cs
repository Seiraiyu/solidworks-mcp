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
