using System;
using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class AddinOperationsTests
{
    static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    sealed class Ctx : IOperationContext
    {
        public string Method => "addin.state";
        public DateTime DeadlineUtc => T0.AddSeconds(5);
        public ILog Log => NullLog.Instance;
    }

    [Fact]
    public void State_reports_running_queued_pumping_and_versions()
    {
        var now = T0;
        var runtime = new AgentRuntime("1.2.3", 4242, new MemoryLog(), () => now);
        var reply = new CapturingReply();
        runtime.Ledger.Enqueue(Requests.Echo(1, "a"), T0, reply);
        runtime.Ledger.TryStart(T0, out _, out _);
        runtime.Ledger.Enqueue(Requests.Other(2), T0.AddMilliseconds(200), reply);
        runtime.LastPumpedUtc = () => T0.AddMilliseconds(100);
        runtime.WatchdogAlive = () => true;
        now = T0.AddMilliseconds(1000);

        var state = (AddinStateResult)((IOperation)new AddinStateOperation(runtime)).Run(new AddinStateRequest(), new Ctx());

        Assert.Equal("1.2.3", state.AddinVersion);
        Assert.Equal(Protocol.Version, state.ProtocolVersion);
        Assert.Equal(4242, state.ProcessId);
        Assert.Equal(1L, state.Running!.Id);
        Assert.Equal(1000L, state.Running.RunningMs);
        Assert.False(state.Running.Answered);
        Assert.Equal(2L, state.Queued.Single().Id);
        Assert.Equal(800L, state.Queued.Single().WaitedMs);
        Assert.Equal(900L, state.UiLastPumpedMs);
        Assert.True(state.WatchdogAlive);
        Assert.False(state.PreferencesApplied);
        Assert.Empty(state.PreferencesNotApplied);
        Assert.Empty(state.Documents);
        Assert.Null(state.DocumentsAsOf);
        Assert.False(state.DiagnosticsEnabled);
    }

    [Fact]
    public void State_says_whether_diagnostics_are_on_in_this_load()
    {
        var runtime = new AgentRuntime("1.2.3", 4242, new MemoryLog(), () => T0, diagnosticsEnabled: true);
        var state = (AddinStateResult)((IOperation)new AddinStateOperation(runtime)).Run(new AddinStateRequest(), new Ctx());
        Assert.True(state.DiagnosticsEnabled);
    }

    [Fact]
    public void Events_returns_what_the_hub_holds()
    {
        var runtime = new AgentRuntime("1", 1, new MemoryLog(), () => T0);
        runtime.Events.Publish("session.closing", new SessionClosingEvent { Reason = "a" });
        runtime.Events.Publish("session.closing", new SessionClosingEvent { Reason = "b" });
        var result = (AddinEventsResult)((IOperation)new AddinEventsOperation(runtime)).Run(new AddinEventsRequest { AfterSeq = 1 }, new Ctx());
        Assert.Equal(new[] { 2L }, result.Events.Select(e => e.Seq).ToArray());
        Assert.False(result.Missed);
        Assert.Equal(2L, result.LastSeq);
    }

    [Fact]
    public void Both_are_pipe_operations()
    {
        var runtime = new AgentRuntime("1", 1, new MemoryLog());
        Assert.IsAssignableFrom<IPipeOperation>(new AddinStateOperation(runtime));
        Assert.IsAssignableFrom<IPipeOperation>(new AddinEventsOperation(runtime));
    }
}
