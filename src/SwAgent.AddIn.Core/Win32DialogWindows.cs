using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Finds SOLIDWORKS' modal dialogs through Win32 only. A modal dialog disables its owner, so one is open when the main
/// window is disabled and a visible, enabled, captioned window of the UI thread is owned (directly or through another
/// dialog) by it. Text is read with WM_GETTEXT under a 500 ms timeout, so a hung UI thread cannot hang the watch.
/// </summary>
public sealed class Win32DialogWindows : IDialogWindows
{
    const int GwOwner = 4;
    const int GwlStyle = -16;
    const int GwlExStyle = -20;
    const long WsCaption = 0x00C00000L;
    const long WsExToolWindow = 0x00000080L;
    const long BsTypeMask = 0xF;
    const long BsPushButton = 0;
    const long BsDefPushButton = 1;
    const long SsTypeMask = 0x1F;
    const long SsIcon = 0x3;
    const long SsBitmap = 0xE;
    const int WmGetText = 0x000D;
    const int WmClose = 0x0010;
    const int WmCommand = 0x0111;
    const int BnClicked = 0;
    const int SmtoAbortIfHung = 0x2;
    const int TextTimeoutMs = 500;
    const int MaxTextEntries = 20;
    const int MaxTextLength = 500;

    readonly IntPtr _frame;
    readonly uint _uiThreadId;

    /// <param name="frame">SOLIDWORKS' main frame window.</param>
    /// <param name="uiThreadId">The native id of the thread that owns it (<see cref="CurrentThreadId"/> on the UI thread).</param>
    public Win32DialogWindows(IntPtr frame, uint uiThreadId)
    {
        _frame = frame;
        _uiThreadId = uiThreadId;
    }

    /// <summary>The calling thread's native id.</summary>
    public static uint CurrentThreadId() => GetCurrentThreadId();

    public IReadOnlyList<IntPtr> FindModal()
    {
        var found = new List<IntPtr>();
        if (_frame == IntPtr.Zero || !IsWindow(_frame) || IsWindowEnabled(_frame)) return found;
        EnumWindowsProc visit = (hwnd, _) =>
        {
            if (IsCandidate(hwnd)) found.Add(hwnd);
            return true;
        };
        EnumThreadWindows(_uiThreadId, visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        return found;
    }

    public NativeDialog? Read(IntPtr dialog)
    {
        if (!IsWindow(dialog)) return null;
        var text = new List<string>();
        var buttons = new List<NativeButton>();
        EnumWindowsProc visit = (child, _) =>
        {
            // Direct children only: a property-sheet dialog's pages hold hundreds of labels that are not its message.
            if (GetParent(child) != dialog || !IsWindowVisible(child)) return true;
            string cls = ClassOf(child);
            long style = Style(child, GwlStyle);
            if (string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase))
            {
                long type = style & SsTypeMask;
                if (type != SsIcon && type != SsBitmap && text.Count < MaxTextEntries)
                {
                    string s = TextOf(child).Trim();
                    if (s.Length > 0) text.Add(s.Length > MaxTextLength ? s.Substring(0, MaxTextLength) : s);
                }
            }
            else if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase))
            {
                long type = style & BsTypeMask;
                if (type == BsPushButton || type == BsDefPushButton)
                    buttons.Add(new NativeButton(child, GetDlgCtrlID(child), TextOf(child)));
            }
            return true;
        };
        EnumChildWindows(dialog, visit, IntPtr.Zero);
        GC.KeepAlive(visit);
        return new NativeDialog(dialog, TextOf(dialog), text, buttons);
    }

    public bool IsOpen(IntPtr dialog) => IsWindow(dialog) && IsWindowVisible(dialog);

    public bool Press(NativeDialog dialog, NativeButton button) =>
        PostMessage(dialog.Hwnd, WmCommand, new IntPtr((BnClicked << 16) | (button.ControlId & 0xFFFF)), button.Hwnd);

    public bool Close(NativeDialog dialog) => PostMessage(dialog.Hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

    bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == _frame || !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return false;
        if ((Style(hwnd, GwlStyle) & WsCaption) != WsCaption || (Style(hwnd, GwlExStyle) & WsExToolWindow) != 0) return false;
        for (IntPtr owner = GetWindow(hwnd, GwOwner); owner != IntPtr.Zero; owner = GetWindow(owner, GwOwner))
        {
            if (owner == _frame) return true;
        }
        return false;
    }

    static long Style(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index).ToInt64() : GetWindowLong32(hwnd, index);

    static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    static string TextOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(2048);
        return SendMessageTimeout(hwnd, WmGetText, new IntPtr(sb.Capacity), sb, SmtoAbortIfHung, TextTimeoutMs, out _) == IntPtr.Zero
            ? ""
            : sb.ToString();
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetDlgCtrlID(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam, int flags, int timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
