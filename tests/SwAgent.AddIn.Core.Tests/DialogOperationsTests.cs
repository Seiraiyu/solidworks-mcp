using System;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DialogOperationsTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    sealed class Ctx : IOperationContext
    {
        public string Method => "dialog.respond";
        public DateTime DeadlineUtc => T0.AddSeconds(10);
        public ILog Log => NullLog.Instance;
    }

    [Fact]
    public void Respond_without_a_dialog_watch_is_invalid_argument()
    {
        IOperation op = new DialogRespondOperation(new AgentRuntime("t", 1, new MemoryLog(), () => T0));
        var e = Assert.Throws<SwAgentException>(() => op.Run(new DialogRespondRequest { Dialog = "d1-1", Button = "OK" }, new Ctx()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
    }

    [Fact]
    public void Respond_goes_to_the_runtimes_dialog_watch()
    {
        var runtime = new AgentRuntime("t", 1, new MemoryLog(), () => T0);
        var windows = new FakeWindows();
        runtime.Dialogs = new DialogWatch(windows, runtime.Ledger, runtime.Events, runtime.Log, runtime.UtcNow);
        windows.Show(Dialogs.Box(10, "Q", "OK"));
        runtime.Dialogs.Tick();
        IOperation op = new DialogRespondOperation(runtime);
        var request = new DialogRespondRequest { Dialog = runtime.Dialogs.Current!.Id, Button = "ok" };
        var result = (DialogRespondResult)op.Run(request, new Ctx());
        op.Verify(request, result, new Ctx());
        Assert.Equal("OK", result.Pressed);
        Assert.IsAssignableFrom<IPipeOperation>(op);
    }

    [Fact]
    public void State_reports_the_open_dialog_and_the_watch()
    {
        var runtime = new AgentRuntime("t", 1, new MemoryLog(), () => T0);
        var windows = new FakeWindows();
        runtime.Dialogs = new DialogWatch(windows, runtime.Ledger, runtime.Events, runtime.Log, runtime.UtcNow);
        windows.Show(Dialogs.Box(11, "Options", "OK", "Cancel"));
        runtime.Dialogs.Tick();
        var state = (AddinStateResult)((IOperation)new AddinStateOperation(runtime)).Run(new AddinStateRequest(), new Ctx());
        Assert.Equal("Options", state.Dialog!.Title);
        Assert.False(state.DialogWatchAlive); // never started in this test
    }

    [Fact]
    public void Diag_dialog_is_refused_while_diagnostics_are_off()
    {
        IOperation op = new DiagDialogOperation(new AgentRuntime("t", 1, new MemoryLog()) { MainWindow = new IntPtr(1) });
        var e = Assert.Throws<SwAgentException>(() => op.Run(new DiagDialogRequest { Title = "t", Text = "x" }, new Ctx()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(@"HKCU\Software\solidworks-mcp", e.Message);
    }

    [Fact]
    public void Diag_dialog_needs_the_main_window()
    {
        IOperation op = new DiagDialogOperation(new AgentRuntime("t", 1, new MemoryLog(), diagnosticsEnabled: true));
        Assert.Equal(ErrorCode.InvalidArgument,
            Assert.Throws<SwAgentException>(() => op.Run(new DiagDialogRequest { Title = "t", Text = "x" }, new Ctx())).Code);
    }

    [Fact]
    public async Task Diag_dialog_shows_a_message_box_owned_by_the_main_window_and_reports_the_button()
    {
        using var ui = new UiThread();
        var owner = OwnerWindow.Create(ui);
        var runtime = new AgentRuntime("t", 1, new MemoryLog(), diagnosticsEnabled: true) { MainWindow = owner };
        IOperation op = new DiagDialogOperation(runtime);
        var request = new DiagDialogRequest { Title = "swmcp diag test", Text = "Answer me.", Buttons = DiagDialogButtons.YesNo };
        var done = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Context.Post(_ => done.SetResult(op.Run(request, new Ctx())), null);

        var windows = new Win32DialogWindows(owner, ui.Invoke(() => Win32DialogWindows.CurrentThreadId()));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (windows.FindModal().Count == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
        var dialog = windows.Read(windows.FindModal()[0])!;
        Assert.Equal("swmcp diag test", dialog.Title);
        Assert.True(windows.Press(dialog, DialogButtons.Find(dialog, "No")!));

        var result = (DiagDialogResult)await DialogWin32Tests.Within(done.Task);
        op.Verify(request, result, new Ctx());
        Assert.Equal("no", result.Pressed);
    }
}
