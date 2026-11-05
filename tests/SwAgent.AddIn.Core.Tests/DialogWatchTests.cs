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
}
