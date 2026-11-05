using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>The fault suite's run: its own id (…-fault-suite), directory, scratch directory and report.</summary>
public sealed class FaultSuiteRun : LiveRun
{
    public FaultSuiteRun()
        : base("fault-suite")
    {
    }
}

/// <summary>
/// The live fault suite (design 363, Testing): every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly
/// as swmcp serve does, in [Step] order, sharing one SOLIDWORKS and one supervisor. Skipped unless SWMCP_LIVE=1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed partial class FaultSuite : IClassFixture<FaultSuiteRun>
{
    static readonly TimeSpan Short = TimeSpan.FromSeconds(10);
    readonly LiveRun _run;

    public FaultSuite(FaultSuiteRun run)
    {
        _run = run;
    }

    Supervisor Sw => _run.Supervisor;

    Task<TResult> Call<TRequest, TResult>(TRequest request, TimeSpan deadline)
        where TRequest : class
        where TResult : class =>
        Sw.CallAsync<TRequest, TResult>(request, deadline);

    async Task WaitUntilRunningAsync(string method) =>
        await LiveRun.WaitForAsync(async () => (await _run.AddinStateAsync()).Running?.Method == method, Short, $"{method} starting").ConfigureAwait(false);

    [SkippableFact, Step(1)]
    public Task The_first_call_loads_the_addin() => _run.CaseAsync(1, "the first call loads the add-in", async () =>
    {
        int pid = await _run.PrepareSolidWorksAsync();
        bool pipeBefore = SystemPipeDirectory.Instance.Exists(Protocol.PipeName(pid));
        if (pipeBefore)
            throw new SkipException($"the add-in is already loaded in SOLIDWORKS {pid} (with diagnostics on), so its first load cannot be timed in this run");
        using (var process = Process.GetProcessById(pid))
        {
            if (process.MainWindowTitle.Contains(" - [", StringComparison.Ordinal))
                throw _run.Stop($"SOLIDWORKS {pid} shows a document ('{process.MainWindowTitle}'); nothing was loaded");
        }

        var clock = Stopwatch.StartNew();
        SessionStatusResult status;
        while (true)
        {
            try
            {
                status = await Call<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), TimeSpan.FromSeconds(60));
                break;
            }
            catch (SwAgentException e) when ((e.Code == ErrorCode.SwStarting || e.Code == ErrorCode.Busy) && clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(2000);
            }
        }
        long loadMs = clock.ElapsedMilliseconds;
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        try
        {
            await Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 1 }, Short);
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.InvalidArgument)
        {
            throw _run.Stop("diagnostic operations are off in this add-in load: " + e.Message);
        }
        _run.SwRevision = status.SwRevision;
        _run.AddinVersion = status.AddinVersion;
        Assert.Equal(pid, status.ProcessId);
        Assert.True(SystemPipeDirectory.Instance.Exists(Protocol.PipeName(pid)));
        return $"process {pid}: pipe before {pipeBefore}, after True; first call answered in {loadMs} ms; SOLIDWORKS {status.SwRevision}, add-in {status.AddinVersion}; documents open: {state.Documents.Count}";
    });

    [SkippableFact, Step(2)]
    public Task Preferences_are_applied_per_connection() => _run.CaseAsync(2, "preferences applied on connect, restored on disconnect", async () =>
    {
        var state = await _run.AddinStateAsync();
        Assert.True(state.PreferencesApplied, "preferences are not applied while connected");
        Assert.Empty(state.PreferencesNotApplied);
        Assert.True(File.Exists(LiveRun.JournalPath), "no preference journal while connected");

        _run.ReplaceSupervisor();
        await LiveRun.WaitForAsync(() => Task.FromResult(!File.Exists(LiveRun.JournalPath)), TimeSpan.FromSeconds(15),
            "the journal being deleted after the client disconnected (preferences restored)");

        var again = await _run.AddinStateAsync();
        Assert.True(again.PreferencesApplied);
        Assert.True(File.Exists(LiveRun.JournalPath));
        return "applied with a journal while connected; journal deleted after disconnect (restored); applied again on the next connection";
    });

    [SkippableFact, Step(3)]
    public Task Status_answers_while_the_UI_thread_is_held() => _run.CaseAsync(3, "sw_status answers during diag.sleep", async () =>
    {
        var sleeping = Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 8000 }, TimeSpan.FromSeconds(30));
        await WaitUntilRunningAsync("diag.sleep");
        var clock = Stopwatch.StartNew();
        var status = await Sw.StatusAsync(TimeSpan.FromSeconds(5));
        long answeredMs = clock.ElapsedMilliseconds;
        Assert.Equal(SupervisorState.Ready, status.State);
        Assert.Equal("diag.sleep", status.Addin!.Running!.Method);
        Assert.Null(status.Session);
        Assert.True(answeredMs < 2000, $"sw_status took {answeredMs} ms");
        var slept = await sleeping;
        Assert.True(slept.SleptMs >= 8000);
        return $"answered in {answeredMs} ms with running={status.Addin.Running.Method} ({status.Addin.Running.RunningMs} ms), responding={status.Responding}; the sleep then finished after {slept.SleptMs} ms";
    });

    [SkippableFact, Step(4)]
    public Task Busy_and_timeout_then_the_late_outcome() => _run.CaseAsync(4, "busy, timeout and operation.completed", async () =>
    {
        long cursor = Sw.Events.LastSeq;
        var overrun = Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 4000 }, TimeSpan.FromMilliseconds(1000));
        await WaitUntilRunningAsync("diag.sleep");
        var queued = Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 10 }, TimeSpan.FromMilliseconds(500));

        var busy = await Assert.ThrowsAsync<SwAgentException>(() => queued);
        Assert.Equal(ErrorCode.Busy, busy.Code);
        var running = Assert.IsType<JsonObject>(busy.Error.Details!["running"]);
        Assert.Equal("diag.sleep", running["method"]);

        var timeout = await Assert.ThrowsAsync<SwAgentException>(() => overrun);
        Assert.Equal(ErrorCode.Timeout, timeout.Code);
        Assert.Equal(true, timeout.Error.Details!["started"]);

        EventEnvelope? completed = null;
        await LiveRun.WaitForAsync(() =>
        {
            completed = Sw.ReadEvents(new SwEventsRequest { AfterSeq = cursor, Kinds = new List<string> { "operation.completed" } })
                .Events.FirstOrDefault(e => (string?)e.Data["method"] == "diag.sleep" && (string?)e.Data["answered_as"] == "timeout");
            return Task.FromResult(completed != null);
        }, TimeSpan.FromSeconds(10), "operation.completed for the diag.sleep answered timeout");
        Assert.Equal(true, completed!.Data["ok"]);
        return $"queued call: busy naming {running["method"]} ({running["running_ms"]} ms); running call: timeout, started=true; " +
               $"then operation.completed ok after {completed.Data["duration_ms"]} ms";
    });

    [SkippableFact, Step(5)]
    public Task A_dialog_during_an_operation_is_answered() => _run.CaseAsync(5, "dialog during an operation, answered with sw_dialog_respond", async () =>
    {
        var shown = Call<DiagDialogRequest, DiagDialogResult>(
            new DiagDialogRequest { Title = "swmcp live 2.D", Text = "Fault suite: a dialog during an operation", Buttons = DiagDialogButtons.OkCancel },
            TimeSpan.FromSeconds(10));
        var open = await Assert.ThrowsAsync<SwAgentException>(() => shown);
        Assert.Equal(ErrorCode.DialogOpen, open.Code);
        Assert.Equal(true, open.Error.Details!["started"]);

        var dialog = (await _run.AddinStateAsync()).Dialog!;
        Assert.Equal("swmcp live 2.D", dialog.Title);
        var answer = await Call<DialogRespondRequest, DialogRespondResult>(
            new DialogRespondRequest { Dialog = dialog.Id, Button = "Cancel", WaitMs = 5000 }, TimeSpan.FromSeconds(15));
        Assert.Equal(DialogOutcome.Result, answer.Outcome);
        Assert.Equal("cancel", answer.Result!["pressed"]);
        return $"dialog_open (started=true) for '{dialog.Title}' with buttons [{string.Join(", ", dialog.Buttons.Select(b => b.Text))}]; " +
               $"pressed {answer.Pressed}; the operation's result: pressed={answer.Result["pressed"]}";
    });

    [SkippableFact, Step(6)]
    public Task A_dialog_with_nothing_running_blocks_new_requests() => _run.CaseAsync(6, "a dialog opened by hand blocks new requests", async () =>
    {
        _run.Say("ACTION: open Tools > Options in SOLIDWORKS now (waiting up to 3 min); the suite closes it with sw_dialog_respond");
        DialogInfo? dialog = null;
        var clock = Stopwatch.StartNew();
        while (dialog == null && clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            var state = await _run.AddinStateAsync();
            if (state.Dialog != null && state.Dialog.RequestId == null) dialog = state.Dialog;
            else await Task.Delay(1000);
        }
        if (dialog == null) throw new SkipException("no dialog was opened by hand within 3 min");

        var blocked = await Assert.ThrowsAsync<SwAgentException>(() =>
            Call<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), Short));
        Assert.Equal(ErrorCode.DialogOpen, blocked.Code);
        Assert.Equal(false, blocked.Error.Details!["started"]);

        var answer = await Call<DialogRespondRequest, DialogRespondResult>(new DialogRespondRequest { Dialog = dialog.Id, Button = "close" }, Short);
        Assert.Equal(DialogOutcome.NoRequest, answer.Outcome);
        await LiveRun.WaitForAsync(async () => (await _run.AddinStateAsync()).Dialog == null, Short, "the dialog closing");
        var after = await Call<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), Short);
        return $"'{dialog.Title}' seen with no request; session.status answered dialog_open (started=false); closed with {answer.Pressed} (outcome no_request); " +
               $"session.status then ran (active document: {after.ActiveDocument?.Title ?? "none"})";
    });

    [SkippableFact, Step(7)]
    public Task A_second_client_is_refused_busy() => _run.CaseAsync(7, "two clients at once", async () =>
    {
        var status = await Sw.StatusAsync(Short);
        Assert.Equal(SupervisorState.Ready, status.State);
        var refused = await Assert.ThrowsAsync<SwAgentException>(() =>
            SwAgentClient.ConnectAsync(new ClientOptions { ProcessId = status.ProcessId }));
        Assert.Equal(ErrorCode.Busy, refused.Code);
        Assert.Contains("another client", refused.Message);
        var still = await Sw.StatusAsync(Short);
        Assert.Equal(SupervisorState.Ready, still.State);
        return $"second client refused: busy, '{refused.Message}'; the first still ready";
    });

    [SkippableFact, Step(8)]
    public Task The_client_dropping_the_pipe_mid_operation() => _run.CaseAsync(8, "the pipe dropped by the client mid-operation", async () =>
    {
        var sleeping = Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 5000 }, TimeSpan.FromSeconds(30));
        await WaitUntilRunningAsync("diag.sleep");
        _run.ReplaceSupervisor();
        var dropped = await Assert.ThrowsAnyAsync<Exception>(() => sleeping);

        SwStatusResult? status = null;
        await LiveRun.WaitForAsync(async () =>
        {
            status = await Sw.StatusAsync(TimeSpan.FromSeconds(15));
            return status.State == SupervisorState.Ready && status.Addin?.Running == null;
        }, TimeSpan.FromSeconds(30), "a new connection with nothing running");
        Assert.True(File.Exists(LiveRun.JournalPath));
        return $"the dropped call ended with {dropped.GetType().Name} ('{dropped.Message}'); a new connection was served once the sleep ended " +
               $"(state {WireNames.ForEnum(status!.State)}, preferences applied again)";
    });
}
