using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Mcp.Tests;

/// <summary>The supervisor against fakes: the test process stands in for SOLIDWORKS, a FakeAddin for its add-in.</summary>
public class SupervisorTests
{
    static readonly int Pid = Environment.ProcessId;

    /// <summary>A supervisor whose SOLIDWORKS is the test process, its add-in <paramref name="addin"/> (served once loaded, or at once).</summary>
    sealed class Rig : IDisposable
    {
        public Rig(FakeAddin? addin, bool loaded = true, int[]? processes = null)
        {
            Addin = addin;
            Loaded = loaded;
            Attach = new FakeAttach((_, _) => { Loaded = true; return 0; });
            Options = new SupervisorOptions
            {
                Client = new ClientOptions
                {
                    Processes = new FakeProcesses(processes ?? new[] { Pid }),
                    Pipes = new FakePipes(n => Loaded && Addin != null && n == Addin.PipeName),
                    PipeNameFor = _ => Addin?.PipeName ?? "none",
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ResponseGrace = TimeSpan.FromMilliseconds(100),
                },
                SolidWorks = Attach,
                Registration = new FakeRegistration(),
                PipeWait = TimeSpan.FromMilliseconds(300),
            };
            Supervisor = new Supervisor(Options);
        }

        public FakeAddin? Addin { get; set; }
        public bool Loaded { get; set; }
        public FakeAttach Attach { get; }
        public SupervisorOptions Options { get; }
        public Supervisor Supervisor { get; }

        public Task<object?> StatusCall() => Supervisor.CallAsync("session.status", new JsonObject());

        public void Dispose() => Supervisor.Dispose();
    }

    static async Task<SwAgentException> Fails(Task task) => await Assert.ThrowsAsync<SwAgentException>(() => task);

    // ---- states, loading and calls ----

    [Fact]
    public async Task Without_SOLIDWORKS_a_call_is_sw_not_running()
    {
        using var rig = new Rig(null, processes: Array.Empty<int>());
        Assert.Equal(ErrorCode.SwNotRunning, (await Fails(rig.StatusCall())).Code);
        Assert.Equal(SupervisorState.NotRunning, rig.Supervisor.State);
    }

    [Fact]
    public async Task Two_SOLIDWORKS_are_sw_multiple_instances_until_one_is_named()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, processes: new[] { Pid, Pid + 100000 });
        Assert.Equal(ErrorCode.SwMultipleInstances, (await Fails(rig.StatusCall())).Code);
        Assert.Equal(SupervisorState.Multiple, rig.Supervisor.State);
        var status = await rig.Supervisor.ReconnectAsync(Pid);
        Assert.Equal(SupervisorState.Ready, status.State);
        Assert.NotNull(await rig.StatusCall());
    }

    [Fact]
    public async Task A_SOLIDWORKS_still_starting_is_sw_starting()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, loaded: false);
        rig.Attach.Readiness = new SolidWorksReadiness(false, "it shows no SOLIDWORKS main window yet");
        Assert.Equal(ErrorCode.SwStarting, (await Fails(rig.StatusCall())).Code);
        Assert.Equal(SupervisorState.Starting, rig.Supervisor.State);
        Assert.Equal(0, rig.Attach.Loads);
    }

    [Fact]
    public async Task The_first_call_loads_the_addin_and_then_runs()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, loaded: false);
        var result = (JsonObject)(await rig.StatusCall())!;
        Assert.Equal("34.4.0", result["sw_revision"]);
        Assert.Equal(1, rig.Attach.Loads);
        Assert.Equal(SupervisorState.Ready, rig.Supervisor.State);
        Assert.NotNull(await rig.StatusCall());
        Assert.Equal(1, rig.Attach.Loads);
    }

    [Fact]
    public async Task A_failed_load_is_not_retried_for_that_process_until_reconnect()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, loaded: false);
        var attach = new FakeAttach((_, _) => 2);
        rig.Options.SolidWorks = attach;
        using var supervisor = new Supervisor(rig.Options);
        Assert.Equal(ErrorCode.AddinNotLoaded, (await Fails(supervisor.CallAsync("session.status", new JsonObject()))).Code);
        Assert.Equal(ErrorCode.AddinNotLoaded, (await Fails(supervisor.CallAsync("session.status", new JsonObject()))).Code);
        Assert.Equal(1, attach.Loads);
        Assert.Equal(SupervisorState.AddinUnavailable, supervisor.State);
        await supervisor.ReconnectAsync(null);
        Assert.Equal(2, attach.Loads);
    }

    [Fact]
    public async Task Calls_are_not_serialized_so_status_answers_while_a_call_waits()
    {
        using var addin = new FakeAddin(r => r.Method == "diag.sleep" ? null : RpcCodecSuccess(r));
        using var rig = new Rig(addin);
        var sleeping = rig.Supervisor.CallAsync("diag.sleep", new JsonObject { ["ms"] = 30000L }, TimeSpan.FromSeconds(30));
        addin.Next("diag.sleep");
        var clock = Stopwatch.StartNew();
        var status = await rig.Supervisor.StatusAsync();
        Assert.Equal(SupervisorState.Ready, status.State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False(sleeping.IsCompleted);
    }

    [Fact]
    public async Task Events_are_kept_with_their_process()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin);
        await rig.StatusCall();
        await addin.SendEventAsync(1, "document.saved", new DocumentEvent { Title = "Part1", Path = @"C:\p\Part1.SLDPRT" });
        SwEventsResult events = rig.Supervisor.ReadEvents(new SwEventsRequest());
        for (int i = 0; i < 100 && events.Events.Count == 0; i++)
        {
            await Task.Delay(20);
            events = rig.Supervisor.ReadEvents(new SwEventsRequest());
        }
        var e = Assert.Single(events.Events);
        Assert.Equal("document.saved", e.Kind);
        Assert.Equal("Part1", e.Data["title"]);
        Assert.Equal(Pid, events.ProcessId);
    }

    [Fact]
    public async Task Reconnecting_to_the_same_process_catches_up_on_missed_events()
    {
        using var first = new FakeAddin();
        using var rig = new Rig(first);
        await rig.StatusCall();
        await first.SendEventAsync(1, "document.opened", new DocumentEvent { Title = "A" });
        await WaitFor(() => rig.Supervisor.Events.LastAddinSeq == 1);
        first.Disconnect();
        await WaitFor(() => !rig.Supervisor.IsConnected);

        using var second = new FakeAddin();
        second.Held.Add(new EventEnvelope { Seq = 2, Kind = "document.saved", Time = "t2", Data = new JsonObject { ["title"] = "A" } });
        second.State.LastEventSeq = 2;
        rig.Addin = second;
        await rig.StatusCall();
        Assert.Equal(new[] { "document.opened", "document.saved" }, rig.Supervisor.ReadEvents(new SwEventsRequest()).Events.Select(e => e.Kind).ToArray());
        Assert.False(rig.Supervisor.ReadEvents(new SwEventsRequest()).Missed);
    }

    // ---- sw_status ----

    [Fact]
    public async Task Status_without_SOLIDWORKS_is_a_state_not_an_error()
    {
        using var rig = new Rig(null, processes: Array.Empty<int>());
        var status = await rig.Supervisor.StatusAsync();
        Assert.Equal(SupervisorState.NotRunning, status.State);
        Assert.Equal(ErrorCode.SwNotRunning, status.Error!.Code);
        Assert.Contains("3DEXPERIENCE dashboard", status.Message);
        Assert.Empty(status.ProcessIds);
        Assert.False(string.IsNullOrEmpty(status.SwmcpVersion));
    }

    [Fact]
    public async Task Status_while_SOLIDWORKS_starts_is_starting()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, loaded: false);
        rig.Attach.Readiness = new SolidWorksReadiness(false, "it is not in the Running Object Table yet");
        var status = await rig.Supervisor.StatusAsync();
        Assert.Equal(SupervisorState.Starting, status.State);
        Assert.Equal(Pid, status.ProcessId);
        Assert.Contains("Running Object Table", status.Message);
    }

    [Fact]
    public async Task Status_when_idle_has_the_addin_state_and_the_session()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin);
        var status = await rig.Supervisor.StatusAsync();
        Assert.Equal(SupervisorState.Ready, status.State);
        Assert.True(status.Responding);
        Assert.Equal("0.1.0-fake", status.Addin!.AddinVersion);
        Assert.Equal("Part1", status.Session!.ActiveDocument!.Title);
        Assert.Contains("is ready", status.Message);
    }

    [Fact]
    public async Task Status_while_a_request_runs_skips_the_session_and_says_what_runs()
    {
        using var addin = new FakeAddin();
        addin.State.Running = new RunningRequestInfo { Id = 4, Method = "diag.sleep", RunningMs = 2500 };
        using var rig = new Rig(addin);
        var status = await rig.Supervisor.StatusAsync();
        Assert.Null(status.Session);
        Assert.True(status.Responding);
        Assert.Contains("running diag.sleep", status.Message);
    }

    [Fact]
    public async Task Status_says_not_responding_when_the_UI_thread_stalls_with_nothing_running_or_the_window_hangs()
    {
        using var addin = new FakeAddin();
        addin.State.UiLastPumpedMs = 6000;
        using var rig = new Rig(addin);
        var stalled = await rig.Supervisor.StatusAsync();
        Assert.False(stalled.Responding);
        Assert.Equal(6000L, stalled.NotRespondingMs);
        Assert.Null(stalled.Session);
        Assert.Contains("never ends SOLIDWORKS", stalled.Message);

        addin.State.UiLastPumpedMs = 10;
        rig.Attach.Hung = true;
        Assert.False((await rig.Supervisor.StatusAsync()).Responding);
    }

    [Fact]
    public async Task Status_with_a_dialog_open_points_to_sw_dialog_respond()
    {
        using var addin = new FakeAddin();
        addin.State.Dialog = new DialogInfo { Id = "d1-9", Title = "Save changes?", Buttons = { new DialogButton { Text = "Yes", ControlId = 6 } }, OpenedAt = "t" };
        using var rig = new Rig(addin);
        var status = await rig.Supervisor.StatusAsync();
        Assert.Null(status.Session);
        Assert.Contains("sw_dialog_respond (dialog d1-9)", status.Message);
    }

    [Fact]
    public async Task Status_while_another_call_loads_the_addin_answers_loading_within_its_deadline()
    {
        using var addin = new FakeAddin();
        using var rig = new Rig(addin, loaded: false);
        using var gate = new System.Threading.ManualResetEventSlim(false);
        rig.Options.SolidWorks = new FakeAttach((_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); rig.Loaded = true; return 0; });
        using var supervisor = new Supervisor(rig.Options);
        var first = supervisor.CallAsync("session.status", new JsonObject(), TimeSpan.FromSeconds(30));
        await WaitFor(() => supervisor.State == SupervisorState.Loading);

        var clock = Stopwatch.StartNew();
        var status = await supervisor.StatusAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.Equal(SupervisorState.Loading, status.State);
        Assert.Equal(ErrorCode.Busy, status.Error!.Code);

        gate.Set();
        Assert.NotNull(await first);
    }

    static string RpcCodecSuccess(Contracts.Rpc.RpcRequest r) =>
        Contracts.Rpc.RpcCodec.Success(r.Id, JsonCodec.ToJson(FakeAddin.SampleStatus()));

    static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
