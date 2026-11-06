using System;
using System.Linq;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DialogWatchTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    static (DialogWatch Watch, FakeWindows Windows, CapturingReply Events, RequestLedger Ledger) Make(DialogAllowList? allowList = null)
    {
        var windows = new FakeWindows();
        var ledger = new RequestLedger();
        var hub = new EventHub(new MemoryLog(), () => T0);
        var events = new CapturingReply();
        hub.Attach(events);
        return (new DialogWatch(windows, ledger, hub, new MemoryLog(), () => T0, allowList), windows, events, ledger);
    }

    static LedgerEntry Running(RequestLedger ledger, long id, CapturingReply reply)
    {
        ledger.Enqueue(Requests.Echo(id, "x"), T0, reply);
        ledger.TryStart(T0, out var entry, out _);
        return entry!;
    }

    [Fact]
    public void Each_dialog_raises_opened_and_closed_and_a_reused_handle_gets_a_new_id()
    {
        var (watch, windows, events, _) = Make();
        var first = Dialogs.Box(100, "First", "OK");
        windows.Show(first);
        watch.Tick();
        string firstId = watch.Current!.Id;
        windows.Hide(first);
        watch.Tick();
        Assert.Null(watch.Current);
        windows.Show(Dialogs.Box(100, "Second", "OK"));
        watch.Tick();
        Assert.NotEqual(firstId, watch.Current!.Id);
        Assert.Equal("Second", watch.Current.Title);
        Assert.Equal(new[] { "dialog.opened", "dialog.closed", "dialog.opened" }, Dialogs.Kinds(events));
    }

    [Fact]
    public void A_dialog_during_a_running_request_answers_it_dialog_open()
    {
        var (watch, windows, _, ledger) = Make();
        var reply = new CapturingReply();
        var entry = Running(ledger, 7, reply);
        windows.Show(Dialogs.Box(200, "SOLIDWORKS", "&Yes", "&No"));
        watch.Tick();

        var answer = reply.Next();
        Assert.Equal(7L, answer.Id);
        Assert.Equal(ErrorCode.DialogOpen, answer.Error!.Code);
        var d = answer.Error.Details!;
        Assert.True(d["started"] is true);
        Assert.True(d["operation_running"] is true);
        Assert.Equal(watch.Current!.Id, ((JsonObject)d["dialog"]!)["id"]);
        Assert.Contains("dialog.respond", answer.Error.Message);
        Assert.Equal(new[] { "Yes", "No" }, watch.Current.Buttons.Select(b => b.Text).ToArray());
        Assert.Equal(7L, watch.Current.RequestId);
        Assert.Equal("dialog_open", entry.AnsweredAs);
    }

    [Fact]
    public void A_dialog_with_nothing_running_answers_nothing()
    {
        var (watch, windows, events, _) = Make();
        windows.Show(Dialogs.Box(300, "Options", "OK", "Cancel"));
        watch.Tick();
        Assert.Null(watch.Current!.RequestId);
        Assert.Equal(new[] { "dialog.opened" }, Dialogs.Kinds(events));
    }

    [Fact]
    public void Respond_presses_by_text_ignoring_case_and_accelerators()
    {
        var (watch, windows, events, _) = Make();
        windows.Show(Dialogs.Box(400, "Save", "&Save", "Do&n't Save", "Cancel"));
        watch.Tick();
        var result = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "don't save" }, T0.AddSeconds(10));
        Assert.Equal("Don't Save", result.Pressed);
        Assert.Equal(2, result.ControlId);
        Assert.Equal(DialogOutcome.NoRequest, result.Outcome);
        Assert.Equal(new[] { "press Do&n't Save" }, windows.Actions);
        Assert.Equal(new[] { "dialog.opened", "dialog.answered", "dialog.closed" }, Dialogs.Kinds(events));
    }

    [Fact]
    public void Respond_presses_by_control_id_and_closes()
    {
        var (watch, windows, _, _) = Make();
        windows.Show(Dialogs.Box(500, "Save", "&Save", "Do&n't Save", "Cancel"));
        watch.Tick();
        Assert.Equal("Cancel", watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "3" }, T0.AddSeconds(10)).Pressed);

        windows.Show(Dialogs.Box(501, "Again", "OK"));
        watch.Tick();
        var closed = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "Close" }, T0.AddSeconds(10));
        Assert.Equal("close", closed.Pressed);
        Assert.Null(closed.ControlId);
        Assert.Equal(new[] { "press Cancel", "close" }, windows.Actions);
    }

    [Fact]
    public void Respond_to_an_unknown_or_already_closed_dialog_is_invalid_argument()
    {
        var (watch, windows, _, _) = Make();
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() =>
            watch.Respond(new DialogRespondRequest { Dialog = "d9-1", Button = "OK" }, T0.AddSeconds(10))).Code);

        var box = Dialogs.Box(600, "Gone", "OK");
        windows.Show(box);
        watch.Tick();
        string id = watch.Current!.Id;
        windows.Hide(box); // closed, and the watch has not ticked since
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() =>
            watch.Respond(new DialogRespondRequest { Dialog = id, Button = "OK" }, T0.AddSeconds(10))).Code);
        Assert.Empty(windows.Actions);
    }

    [Fact]
    public void Respond_with_an_unknown_button_lists_the_buttons()
    {
        var (watch, windows, _, _) = Make();
        windows.Show(Dialogs.Box(700, "Save", "&Save", "Cancel"));
        watch.Tick();
        var e = Assert.Throws<SwAgentException>(() => watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "Maybe" }, T0.AddSeconds(10)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("Save (1), Cancel (2), or close", e.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public void Respond_bounds_wait_ms(int waitMs)
    {
        var (watch, windows, _, _) = Make();
        windows.Show(Dialogs.Box(800, "Q", "OK"));
        watch.Tick();
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() =>
            watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "OK", WaitMs = waitMs }, T0.AddSeconds(100))).Code);
    }

    [Fact]
    public void Respond_waits_for_the_request_and_returns_its_result()
    {
        var (watch, windows, _, ledger) = Make();
        var entry = Running(ledger, 5, new CapturingReply());
        windows.Show(Dialogs.Box(900, "Q", "OK"));
        watch.Tick();
        // A dedicated thread, not the pool: a busy 2-core CI runner can hold a pool work item past the wait.
        windows.AfterAnswer = _ => new Thread(() =>
        {
            Thread.Sleep(50);
            entry.Outcome = RpcResponse.Success(5, new JsonObject { ["text"] = "done" });
            ledger.Finish(entry);
        }) { IsBackground = true }.Start();
        var result = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "OK", WaitMs = DialogWatch.MaxWaitMs }, T0.AddSeconds(100));
        Assert.Equal(DialogOutcome.Result, result.Outcome);
        Assert.Equal("done", result.Result!["text"]);
    }

    [Fact]
    public void Respond_returns_the_requests_error()
    {
        var (watch, windows, _, ledger) = Make();
        var entry = Running(ledger, 5, new CapturingReply());
        windows.Show(Dialogs.Box(901, "Q", "OK"));
        watch.Tick();
        windows.AfterAnswer = _ =>
        {
            entry.Outcome = RpcResponse.Failure(5, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.OperationFailed, "nope"));
            ledger.Finish(entry);
        };
        var result = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "OK" }, T0.AddSeconds(10));
        Assert.Equal(DialogOutcome.Error, result.Outcome);
        Assert.Equal(ErrorCode.OperationFailed, result.Error!.Code);
    }

    [Fact]
    public void Respond_says_still_running_when_the_wait_ends_first()
    {
        var (watch, windows, _, ledger) = Make();
        Running(ledger, 5, new CapturingReply());
        windows.Show(Dialogs.Box(902, "Q", "OK"));
        watch.Tick();
        var result = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "OK", WaitMs = 100 }, T0.AddSeconds(10));
        Assert.Equal(DialogOutcome.StillRunning, result.Outcome);
    }

    [Fact]
    public void Respond_returns_the_next_dialog_the_request_raises()
    {
        var (watch, windows, _, ledger) = Make();
        Running(ledger, 5, new CapturingReply());
        windows.Show(Dialogs.Box(903, "First", "OK"));
        watch.Tick();
        windows.AfterAnswer = _ => windows.Show(Dialogs.Box(904, "Second", "Yes", "No"));
        var result = watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "OK" }, T0.AddSeconds(10));
        Assert.Equal(DialogOutcome.DialogOpen, result.Outcome);
        Assert.Equal("Second", result.NextDialog!.Title);
    }

    [Fact]
    public void Respond_brings_the_table_up_to_date_before_it_answers()
    {
        var (watch, windows, events, _) = Make();
        windows.Show(Dialogs.Box(908, "Options", "OK", "Cancel"));
        watch.Tick();
        watch.Respond(new DialogRespondRequest { Dialog = watch.Current!.Id, Button = "Cancel" }, T0.AddSeconds(10));
        Assert.Null(watch.Current);
        Assert.Equal(new[] { "dialog.opened", "dialog.answered", "dialog.closed" }, Dialogs.Kinds(events));
    }

    [Fact]
    public void A_dialog_on_the_allow_list_is_answered_by_itself_and_its_request_is_not_interrupted()
    {
        var allow = new DialogAllowList(new[] { new DialogRule("rebuild errors", "^Rebuild$", "happened", "Continue") });
        var (watch, windows, events, ledger) = Make(allow);
        var reply = new CapturingReply();
        Running(ledger, 5, reply);
        windows.Show(Dialogs.Box(905, "Rebuild", "Stop", "&Continue"));
        watch.Tick();
        Assert.Equal(new[] { "press &Continue" }, windows.Actions);
        Assert.Empty(reply.Lines);
        Assert.Equal(new[] { "dialog.opened", "dialog.answered" }, Dialogs.Kinds(events));
        var answered = (JsonObject)((JsonObject)((JsonObject)Json.Parse(events.Lines.ToArray()[1])!)["params"]!)["data"]!;
        Assert.Equal("allow_list", answered["by"]);
        Assert.Equal("Continue", answered["button"]);
    }

    [Fact]
    public void An_allow_list_press_that_cannot_be_posted_answers_the_request_dialog_open()
    {
        var allow = new DialogAllowList(new[] { new DialogRule("rebuild errors", "^Rebuild$", null, "Continue") });
        var (watch, windows, events, ledger) = Make(allow);
        windows.FailPosts = true;
        var reply = new CapturingReply();
        Running(ledger, 5, reply);
        windows.Show(Dialogs.Box(906, "Rebuild", "Stop", "Continue"));
        watch.Tick();
        Assert.Equal(ErrorCode.DialogOpen, reply.Next().Error!.Code);
        Assert.Equal(new[] { "dialog.opened" }, Dialogs.Kinds(events));
        Assert.Equal("Rebuild", watch.Current!.Title);
    }

    [Fact]
    public void Current_skips_a_dialog_whose_window_has_gone_before_the_next_pass()
    {
        var (watch, windows, events, _) = Make();
        var box = Dialogs.Box(907, "Gone", "OK");
        windows.Show(box);
        watch.Tick();
        windows.Hide(box);
        Assert.Null(watch.Current);                                // no pass has run since it went
        Assert.Equal(new[] { "dialog.opened" }, Dialogs.Kinds(events));
        watch.Tick();
        Assert.Equal(new[] { "dialog.opened", "dialog.closed" }, Dialogs.Kinds(events));
    }

    [Theory]
    [InlineData("Login - 3DEXPERIENCE ID - Dassault Systemes")]
    [InlineData("Login | 3DEXPERIENCE ID | Dassault Systèmes")]
    [InlineData("License not granted")]
    [InlineData("SOLIDWORKS Sign In")]
    public void A_sign_in_or_licence_window_needs_a_person_and_is_never_answered(string title)
    {
        var allow = new DialogAllowList(new[] { new DialogRule("anything", ".*", null, "OK") });
        var (watch, windows, _, _) = Make(allow);
        windows.Show(Dialogs.Box(500, title, "OK"));
        watch.Tick();
        Assert.True(watch.Current!.NeedsPerson);
        Assert.Empty(windows.Actions);
        var e = Assert.Throws<SwAgentException>(() => watch.Respond(new DialogRespondRequest { Dialog = watch.Current.Id, Button = "close" }, T0.AddSeconds(10)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("needs_person", e.Error.Details!["reason"]);
        Assert.Contains("only a person", e.Message);
        Assert.Throws<SwAgentException>(() => watch.Respond(new DialogRespondRequest { Dialog = watch.Current.Id, Button = "OK" }, T0.AddSeconds(10)));
        Assert.Empty(windows.Actions);
    }

    [Theory]
    [InlineData("System Options - General")]
    [InlineData("SOLIDWORKS")]
    [InlineData("Rebuild Errors")]
    public void Other_dialogs_are_answered_as_before(string title)
    {
        Assert.False(DialogGuards.NeedsPerson(title));
        var (watch, windows, _, _) = Make();
        windows.Show(Dialogs.Box(600, title, "OK"));
        watch.Tick();
        Assert.False(watch.Current!.NeedsPerson);
        watch.Respond(new DialogRespondRequest { Dialog = watch.Current.Id, Button = "close", WaitMs = 0 }, T0.AddSeconds(10));
        Assert.Equal(new[] { "close" }, windows.Actions.ToArray());
    }
}
