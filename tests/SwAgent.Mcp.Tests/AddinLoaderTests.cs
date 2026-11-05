using System;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class AddinLoaderTests
{
    static DateTime Soon => DateTime.UtcNow.AddSeconds(10);

    static AddinLoader Loader(FakeAttach attach, Func<bool> pipeUp, FakeRegistration? registration = null) =>
        new AddinLoader(attach, registration ?? new FakeRegistration(), new FakePipes(n => n == "solidworks-mcp-7" && pipeUp()))
        {
            PipeWait = TimeSpan.FromMilliseconds(300),
        };

    [Fact]
    public async Task A_serving_pipe_needs_no_load()
    {
        var attach = new FakeAttach((_, _) => throw new InvalidOperationException("must not load"));
        var result = await Loader(attach, () => true).EnsureLoadedAsync(7, Soon);
        Assert.True(result.AlreadyLoaded);
        Assert.Equal(0, attach.Loads);
    }

    [Fact]
    public async Task Not_registered_is_addin_not_loaded_saying_register()
    {
        var attach = new FakeAttach((_, _) => 0);
        var registration = new FakeRegistration { Info = new AddinRegistrationInfo(false, null, false) };
        var e = await Assert.ThrowsAsync<SwAgentException>(() => Loader(attach, () => false, registration).EnsureLoadedAsync(7, Soon));
        Assert.Equal(ErrorCode.AddinNotLoaded, e.Code);
        Assert.Contains("swmcp addin register", e.Message);
        Assert.Equal(0, attach.Loads);
    }

    [Fact]
    public async Task A_SOLIDWORKS_still_starting_is_sw_starting_and_is_not_loaded()
    {
        var attach = new FakeAttach((_, _) => 0) { Readiness = new SolidWorksReadiness(false, "it shows no SOLIDWORKS main window yet") };
        var e = await Assert.ThrowsAsync<SwAgentException>(() => Loader(attach, () => false).EnsureLoadedAsync(7, Soon));
        Assert.Equal(ErrorCode.SwStarting, e.Code);
        Assert.Contains("no SOLIDWORKS main window", e.Message);
        Assert.Equal(0, attach.Loads);
    }

    [Fact]
    public async Task Loads_the_registered_dll_and_waits_for_the_pipe()
    {
        bool up = false;
        (int, string)? asked = null;
        var attach = new FakeAttach((pid, path) => { asked = (pid, path); up = true; return 0; });
        var result = await Loader(attach, () => up).EnsureLoadedAsync(7, Soon);
        Assert.False(result.AlreadyLoaded);
        Assert.Equal(0, result.LoadAddInResult);
        Assert.Equal((7, @"C:\addin\SwAgent.AddIn.dll"), asked);
    }

    [Fact]
    public async Task A_pipe_that_never_appears_is_remembered_until_forgotten()
    {
        var attach = new FakeAttach((_, _) => 2);
        var loader = Loader(attach, () => false);
        var first = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, Soon));
        Assert.Equal(ErrorCode.AddinNotLoaded, first.Code);
        Assert.Equal(2L, first.Error.Details!["load_addin_result"]);
        Assert.Contains("sw_reconnect", first.Message);

        var again = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, Soon));
        Assert.Equal(first.Message, again.Message);
        Assert.Equal(1, attach.Loads);

        loader.Forget(7);
        await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, Soon));
        Assert.Equal(2, attach.Loads);
    }

    [Fact]
    public async Task A_deadline_shorter_than_the_load_is_busy_and_the_next_call_waits_without_loading_again()
    {
        bool up = false;
        var attach = new FakeAttach((_, _) => 0);
        var loader = Loader(attach, () => up);
        loader.PipeWait = TimeSpan.FromSeconds(30);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, DateTime.UtcNow.AddMilliseconds(200)));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal(false, e.Error.Details!["started"]);

        _ = Task.Delay(200).ContinueWith(_ => up = true);
        var result = await loader.EnsureLoadedAsync(7, Soon);
        Assert.False(result.AlreadyLoaded);
        Assert.Equal(1, attach.Loads);
    }

    [Fact]
    public async Task A_LoadAddIn_failure_is_api_error_with_the_log_directory()
    {
        var attach = new FakeAttach((_, _) => throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS (process 7) failed ISldWorks.LoadAddIn: E_FAIL"));
        var e = await Assert.ThrowsAsync<SwAgentException>(() => Loader(attach, () => false).EnsureLoadedAsync(7, Soon));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Equal(AddinLoader.LogDirectory, e.Error.Details!["log_directory"]);
    }

    [Fact]
    public async Task LoadAddIn_never_runs_twice_while_one_is_in_flight()
    {
        using var gate = new ManualResetEventSlim(false);
        bool up = false;
        var attach = new FakeAttach((_, _) => { gate.Wait(TimeSpan.FromSeconds(30)); up = true; return 0; });
        var loader = Loader(attach, () => up);
        var first = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, DateTime.UtcNow.AddMilliseconds(200)));
        Assert.Equal(ErrorCode.Busy, first.Code);
        Assert.Equal(true, first.Error.Details!["loading"]);
        Assert.True(loader.IsLoading(7));
        var second = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, DateTime.UtcNow.AddMilliseconds(200)));
        Assert.Equal(ErrorCode.Busy, second.Code);

        gate.Set();
        var done = await loader.EnsureLoadedAsync(7, Soon);
        Assert.Equal(1, attach.Loads);
        Assert.True(done.AlreadyLoaded || done.LoadAddInResult == 0);
    }

    [Fact]
    public async Task A_LoadAddIn_SOLIDWORKS_rejected_as_busy_is_busy_and_not_remembered()
    {
        int calls = 0;
        bool up = false;
        var attach = new FakeAttach((_, _) =>
        {
            if (++calls == 1)
            {
                throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS (process 7) failed ISldWorks.LoadAddIn (0x80010001): call rejected",
                    new JsonObject { ["hresult"] = "0x80010001" });
            }
            up = true;
            return 0;
        });
        var loader = Loader(attach, () => up);
        var e = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, Soon));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("0x80010001", e.Error.Details!["hresult"]);
        await loader.EnsureLoadedAsync(7, Soon); // not remembered: the next call loads
        Assert.Equal(2, attach.Loads);
    }

    [Fact]
    public async Task A_probe_SOLIDWORKS_does_not_answer_within_the_deadline_is_sw_starting()
    {
        using var gate = new ManualResetEventSlim(false);
        var attach = new SlowProbe(gate);
        var loader = new AddinLoader(attach, new FakeRegistration(), new FakePipes(_ => false));
        var e = await Assert.ThrowsAsync<SwAgentException>(() => loader.EnsureLoadedAsync(7, DateTime.UtcNow.AddMilliseconds(300)));
        Assert.Equal(ErrorCode.SwStarting, e.Code);
        Assert.Contains("does not answer yet", e.Message);
        gate.Set();
    }

    /// <summary>A SOLIDWORKS whose readiness probe hangs until released.</summary>
    sealed class SlowProbe : ISolidWorksAttach
    {
        readonly ManualResetEventSlim _gate;

        public SlowProbe(ManualResetEventSlim gate)
        {
            _gate = gate;
        }

        public SolidWorksReadiness Probe(int processId)
        {
            _gate.Wait(TimeSpan.FromSeconds(30));
            return SolidWorksReadiness.Yes;
        }

        public bool IsHung(int processId) => false;

        public int LoadAddIn(int processId, string dllPath) => throw new InvalidOperationException("must not load");
    }
}
