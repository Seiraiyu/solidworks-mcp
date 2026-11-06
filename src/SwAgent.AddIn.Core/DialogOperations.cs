using System;
using System.Windows.Forms;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>dialog.respond: answered on the pipe thread, because the UI thread is inside the dialog's modal loop.</summary>
public sealed class DialogRespondOperation : PipeOperation<DialogRespondRequest, DialogRespondResult>
{
    readonly AgentRuntime _runtime;

    public DialogRespondOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override DialogRespondResult Run(DialogRespondRequest request, IOperationContext context)
    {
        var watch = _runtime.Dialogs ?? throw new SwAgentException(ErrorCode.InvalidArgument,
            "the dialog watch is not running in this add-in (SOLIDWORKS' main window could not be read at load); see the add-in log");
        return watch.Respond(request, context.DeadlineUtc);
    }

    /// <summary>The check is that it answered the dialog it was asked to.</summary>
    protected override void Verify(DialogRespondRequest request, DialogRespondResult result, IOperationContext context)
    {
        if (result.Dialog != request.Dialog)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"dialog.respond answered {result.Dialog}, not {request.Dialog}");
    }
}

/// <summary>diag.dialog: a modal message box owned by SOLIDWORKS' main window, so fault tests can raise a dialog on demand.</summary>
public sealed class DiagDialogOperation : Operation<DiagDialogRequest, DiagDialogResult>
{
    readonly AgentRuntime _runtime;

    public DiagDialogOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override DiagDialogResult Run(DiagDialogRequest request, IOperationContext context)
    {
        if (!_runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (_runtime.MainWindow == IntPtr.Zero)
            throw new SwAgentException(ErrorCode.InvalidArgument, "diag.dialog needs SOLIDWORKS' main window, which the add-in could not read at load");
        context.Log.Warn($"diag.dialog: showing \"{request.Title}\" ({request.Buttons}) over SOLIDWORKS");
        return new DiagDialogResult { Pressed = DiagBoxes.Show(_runtime.MainWindow, request) };
    }

    protected override void Verify(DiagDialogRequest request, DiagDialogResult result, IOperationContext context)
    {
        bool expected = request.Buttons switch
        {
            DiagDialogButtons.OkCancel => result.Pressed == "ok" || result.Pressed == "cancel",
            DiagDialogButtons.YesNo => result.Pressed == "yes" || result.Pressed == "no",
            _ => result.Pressed == "ok",
        };
        if (!expected)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the {request.Buttons} message box reported '{result.Pressed}'");
    }
}

/// <summary>The diagnostic message box diag.dialog and diag.arm_dialog show: modal, owned by SOLIDWORKS' main window, on the calling (UI) thread.</summary>
public static class DiagBoxes
{
    /// <summary>Shows it and waits; the button that closed it: ok, cancel, yes or no.</summary>
    public static string Show(IntPtr owner, DiagDialogRequest request)
    {
        var buttons = request.Buttons switch
        {
            DiagDialogButtons.OkCancel => MessageBoxButtons.OKCancel,
            DiagDialogButtons.YesNo => MessageBoxButtons.YesNo,
            _ => MessageBoxButtons.OK,
        };
        return WireName(MessageBox.Show(new OwnerWindow(owner), request.Text, request.Title, buttons));
    }

    static string WireName(DialogResult result) => result switch
    {
        DialogResult.OK => "ok",
        DialogResult.Cancel => "cancel",
        DialogResult.Yes => "yes",
        DialogResult.No => "no",
        _ => result.ToString().ToLowerInvariant(),
    };

    sealed class OwnerWindow : IWin32Window
    {
        public OwnerWindow(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}

/// <summary>diag.arm_dialog: arms the message box the next model change shows mid-change (see ModelChangeOperation).</summary>
public sealed class DiagArmDialogOperation : Operation<DiagArmDialogRequest, DiagArmDialogResult>
{
    readonly AgentRuntime _runtime;

    public DiagArmDialogOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override DiagArmDialogResult Run(DiagArmDialogRequest request, IOperationContext context)
    {
        if (!_runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (_runtime.MainWindow == IntPtr.Zero)
            throw new SwAgentException(ErrorCode.InvalidArgument, "diag.arm_dialog needs SOLIDWORKS' main window, which the add-in could not read at load");
        _runtime.ArmDialog(new DiagDialogRequest { Title = request.Title, Text = request.Text, Buttons = request.Buttons });
        context.Log.Warn($"diag.arm_dialog: the next model change shows \"{request.Title}\" ({request.Buttons})");
        return new DiagArmDialogResult { Armed = request.Title };
    }

    /// <summary>Nothing to check: the box is shown by the next model change, not by this.</summary>
    protected override void Verify(DiagArmDialogRequest request, DiagArmDialogResult result, IOperationContext context)
    {
    }
}
