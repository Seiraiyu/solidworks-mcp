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

/// <summary>
/// The cases that end SOLIDWORKS while the suite's supervisor stays connected (the case deferred from 2.C). The add-in
/// serves one client and the suite holds it, so hold-and-check.ps1 cannot be used here: the owner guard ends
/// SOLIDWORKS instead, by process id, after reading the open documents again through the suite's own connection.
/// Each ending is followed by the user restarting SOLIDWORKS from the 3DEXPERIENCE tab.
/// </summary>
public sealed partial class FaultSuite
{
    static readonly TimeSpan RestartWait = TimeSpan.FromMinutes(10);

    [SkippableFact, Step(9)]
    public Task SOLIDWORKS_killed_mid_operation_is_sw_crashed() => _run.CaseAsync(9, "SOLIDWORKS killed mid-operation, client connected: sw_crashed", async () =>
    {
        int pid = (await _run.RequireOnlyOwnDocumentsAsync()).ProcessId;
        string path = _run.Guard.NewScratchPath("killed-dirty");
        // Recorded first: a call that times out or fails after SOLIDWORKS opened the part still leaves it known as ours.
        _run.Guard.Created(path);
        var part = await Call<DiagScratchPartRequest, DocumentInfo>(new DiagScratchPartRequest { Path = path }, TimeSpan.FromSeconds(60));
        Assert.True(part.Dirty, "the scratch part is not dirty");
        await LiveRun.WaitForAsync(async () =>
        {
            var status = await Sw.StatusAsync(Short);
            return status.Addin?.Documents.Any(d => d.Dirty && string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)) == true;
        }, Short, "the scratch part listed dirty in the add-in's document table");

        var sleeping = Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 30000 }, TimeSpan.FromSeconds(60));
        long failedAt = 0;
        var observed = sleeping.ContinueWith(_ => failedAt = Stopwatch.GetTimestamp(), TaskContinuationOptions.ExecuteSynchronously);
        await WaitUntilRunningAsync("diag.sleep");
        long checkStarted = Stopwatch.GetTimestamp();
        var kill = await _run.Guard.KillAsync(pid, _run.AddinStateAsync);
        var crashed = await Assert.ThrowsAsync<SwAgentException>(() => sleeping);
        await observed;
        long reportedMs = (long)Stopwatch.GetElapsedTime(kill.KilledAt, failedAt).TotalMilliseconds;
        long exitMs = (long)Stopwatch.GetElapsedTime(kill.KilledAt, kill.ExitedAt).TotalMilliseconds;
        long totalMs = (long)Stopwatch.GetElapsedTime(checkStarted, failedAt).TotalMilliseconds;

        Assert.Equal(ErrorCode.SwCrashed, crashed.Code);
        Assert.True(reportedMs < 1000, $"sw_crashed came {reportedMs} ms after the kill");
        var crash = JsonCodec.FromJson<CrashInfo>(crashed.Error.Details);
        Assert.Equal(pid, crash.ProcessId);
        Assert.Contains(crash.InFlight, c => c.Method == "diag.sleep");
        Assert.Contains(crash.DirtyDocuments, d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(LiveRun.JournalPath), "no preference journal was left by the kill");
        var after = await Sw.StatusAsync(Short);
        Assert.NotNull(after.LastCrash);
        return $"killed process {pid} while diag.sleep held it (guard re-checked the documents: only {Path.GetFileName(path)}); in-flight call answered sw_crashed {reportedMs} ms after the kill " +
               $"(process gone {exitMs} ms after the kill; {totalMs} ms from the guard's check to the answer); exit code {crash.ExitCode}; in flight: {string.Join(", ", crash.InFlight.Select(c => $"{c.Method} ({c.RunningMs} ms)"))}; " +
               $"dirty: {string.Join(", ", crash.DirtyDocuments.Select(d => d.Title))} as of {crash.DocumentsAsOf}; preference journal left; sw_status state {WireNames.ForEnum(after.State)} with last_crash";
    });

    [SkippableFact, Step(10)]
    public Task After_a_crash_the_next_call_reattaches_and_recovers_preferences() => _run.CaseAsync(10, "reattach after the user restarts SOLIDWORKS; preferences recovered", async () =>
    {
        var (status, seen) = await _run.WaitForSolidWorksAsync(RestartWait);
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        Assert.True(state.PreferencesRecovered, "the add-in did not report replaying the preference journal");
        Assert.NotNull(status.LastCrash);
        var reconnected = await Sw.ReconnectAsync(null, TimeSpan.FromSeconds(30));
        Assert.Equal(SupervisorState.Ready, reconnected.State);
        Assert.Null(reconnected.LastCrash);
        return $"states seen: {string.Join(" -> ", seen)}; process {status.ProcessId}, add-in {status.Addin?.AddinVersion}; " +
               $"preferences_recovered=true; last_crash shown until sw_reconnect, then cleared; documents open: {state.Documents.Count}";
    });

    [SkippableFact, Step(11)]
    public Task SOLIDWORKS_closed_while_connected_is_sw_not_running() => _run.CaseAsync(11, "SOLIDWORKS closed normally, client connected: sw_not_running", async () =>
    {
        int pid = (await _run.RequireOnlyOwnDocumentsAsync()).ProcessId;
        long cursor = Sw.Events.LastSeq;
        var clock = Stopwatch.StartNew();
        bool exited = await _run.Guard.CloseAsync(pid, _run.AddinStateAsync, TimeSpan.FromMinutes(2));
        if (!exited) throw _run.Stop($"SOLIDWORKS {pid} did not exit within 2 min of a normal close (a prompt?); it was left as it is, not killed");
        long closedMs = clock.ElapsedMilliseconds;

        var status = await Sw.StatusAsync(Short);
        Assert.Equal(SupervisorState.NotRunning, status.State);
        Assert.Null(status.LastCrash);
        var call = await Assert.ThrowsAsync<SwAgentException>(() => Call<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), Short));
        Assert.Equal(ErrorCode.SwNotRunning, call.Code);
        bool closing = Sw.ReadEvents(new SwEventsRequest { AfterSeq = cursor, Kinds = new List<string> { "session.closing" } }).Events.Count > 0;
        Assert.True(closing, "no session.closing was received before the exit");
        Assert.False(File.Exists(LiveRun.JournalPath), "the preference journal is still there: preferences were not restored at the close");
        return $"closed process {pid} (guard re-checked: no documents); exited after {closedMs} ms; session.closing received; " +
               $"sw_status {WireNames.ForEnum(status.State)} with no last_crash; the next call: {WireNames.ForEnum(call.Code)}; preferences restored (journal gone)";
    });

    [SkippableFact, Step(12)]
    public Task After_a_close_the_next_call_reattaches() => _run.CaseAsync(12, "reattach after the user restarts SOLIDWORKS following a close", async () =>
    {
        var (status, seen) = await _run.WaitForSolidWorksAsync(RestartWait);
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        Assert.False(state.PreferencesRecovered, "a journal was replayed although the close restored the preferences");
        return $"states seen: {string.Join(" -> ", seen)}; process {status.ProcessId}; no journal to recover; documents open: {state.Documents.Count}. SOLIDWORKS is left running for the owner";
    });
}
