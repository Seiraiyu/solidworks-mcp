using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>Real Win32 message boxes on the fake UI thread, owned by a window standing in for SOLIDWORKS' frame.</summary>
public class DialogWin32Tests
{
    static Win32DialogWindows Windows(UiThread ui, IntPtr owner) =>
        new Win32DialogWindows(owner, ui.Invoke(() => Win32DialogWindows.CurrentThreadId()));

    static Task<DialogResult> ShowBox(UiThread ui, IntPtr owner, MessageBoxButtons buttons)
    {
        var done = new TaskCompletionSource<DialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Context.Post(_ => done.SetResult(MessageBox.Show(new OwnerWindow(owner), "The body text.", "swmcp test box", buttons)), null);
        return done.Task;
    }

    static IntPtr WaitForOne(Win32DialogWindows windows)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            var found = windows.FindModal();
            if (found.Count == 1) return found[0];
            Thread.Sleep(20);
        }
        throw new TimeoutException("no modal dialog was found");
    }

    [Fact]
    public void Nothing_is_found_while_the_owner_is_enabled()
    {
        using var ui = new UiThread();
        Assert.Empty(Windows(ui, OwnerWindow.Create(ui)).FindModal());
    }

    [Fact]
    public async Task A_message_box_owned_by_the_frame_is_found_read_and_pressed()
    {
        using var ui = new UiThread();
        var owner = OwnerWindow.Create(ui);
        var windows = Windows(ui, owner);
        var shown = ShowBox(ui, owner, MessageBoxButtons.OKCancel);
        var hwnd = WaitForOne(windows);

        var dialog = windows.Read(hwnd)!;
        Assert.Equal("swmcp test box", dialog.Title);
        Assert.Contains("The body text.", dialog.Text);
        Assert.Equal(new[] { (1, "OK"), (2, "Cancel") }, dialog.Buttons.Select(b => (b.ControlId, b.Text)).OrderBy(b => b.ControlId).ToArray());

        Assert.True(windows.Press(dialog, dialog.Buttons.Single(b => b.ControlId == 2)));
        Assert.Equal(DialogResult.Cancel, await Within(shown));
        Assert.Empty(windows.FindModal());
    }

    [Fact]
    public async Task Close_posts_WM_CLOSE()
    {
        using var ui = new UiThread();
        var owner = OwnerWindow.Create(ui);
        var windows = Windows(ui, owner);
        var shown = ShowBox(ui, owner, MessageBoxButtons.OKCancel);
        var dialog = windows.Read(WaitForOne(windows))!;
        Assert.True(windows.Close(dialog));
        Assert.Equal(DialogResult.Cancel, await Within(shown));
    }

    /// <summary>The task's result, failing the test if it takes more than 10 s.</summary>
    internal static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        return await task;
    }
}
