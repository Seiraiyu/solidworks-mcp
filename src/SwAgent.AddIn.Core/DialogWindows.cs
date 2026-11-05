using System;
using System.Collections.Generic;

namespace SwAgent.AddIn.Core;

/// <summary>A push button as Win32 sees it.</summary>
public sealed class NativeButton
{
    public NativeButton(IntPtr hwnd, int controlId, string text)
    {
        Hwnd = hwnd;
        ControlId = controlId;
        Text = text;
    }

    public IntPtr Hwnd { get; }
    public int ControlId { get; }

    /// <summary>The raw text, & accelerator marks included.</summary>
    public string Text { get; }
}

/// <summary>A modal dialog as Win32 sees it.</summary>
public sealed class NativeDialog
{
    public NativeDialog(IntPtr hwnd, string title, IReadOnlyList<string> text, IReadOnlyList<NativeButton> buttons)
    {
        Hwnd = hwnd;
        Title = title;
        Text = text;
        Buttons = buttons;
    }

    public IntPtr Hwnd { get; }
    public string Title { get; }
    public IReadOnlyList<string> Text { get; }
    public IReadOnlyList<NativeButton> Buttons { get; }
}

/// <summary>The windows the dialog watch finds, reads and presses. Safe from any thread; never calls SOLIDWORKS.</summary>
public interface IDialogWindows
{
    /// <summary>The modal dialogs open now: none while the main window is enabled.</summary>
    IReadOnlyList<IntPtr> FindModal();

    /// <summary>The dialog's title, text and push buttons; null when the window is gone.</summary>
    NativeDialog? Read(IntPtr dialog);

    bool IsOpen(IntPtr dialog);

    /// <summary>Posts the button's command to the dialog, as a click would. False when the post failed.</summary>
    bool Press(NativeDialog dialog, NativeButton button);

    /// <summary>Posts WM_CLOSE to the dialog. False when the post failed.</summary>
    bool Close(NativeDialog dialog);
}
