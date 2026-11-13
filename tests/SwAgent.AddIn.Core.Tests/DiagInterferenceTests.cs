using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DiagInterferenceTests
{
    sealed class Ctx : IOperationContext
    {
        public string Method => "diag.interference";
        public DateTime DeadlineUtc => DateTime.UtcNow.AddMinutes(1);
        public ILog Log => NullLog.Instance;
    }

    [Fact]
    public void Refused_unless_diagnostics_are_on()
    {
        IOperation op = new DiagInterferenceOperation(new AgentRuntime("t", 1, NullLog.Instance));
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => op.Run(new DiagInterferenceRequest(), new Ctx())).Code);
    }

    [Fact]
    public void Arms_a_delay_and_reports_the_sessions()
    {
        var runtime = new AgentRuntime("t", 1, NullLog.Instance, diagnosticsEnabled: true);
        runtime.InterferenceOpened();
        runtime.InterferenceClosed();
        IOperation op = new DiagInterferenceOperation(runtime);
        var r = (DiagInterferenceResult)op.Run(new DiagInterferenceRequest { DelayMs = 3000 }, new Ctx());
        Assert.Equal(1, r.Opened);
        Assert.Equal(1, r.Closed);
        Assert.Equal(3000, r.ArmedMs);
        Assert.Equal(3000, runtime.TakeInterferenceDelay());
        Assert.Equal(0, runtime.TakeInterferenceDelay());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    public void The_delay_is_bounded(int ms)
    {
        IOperation op = new DiagInterferenceOperation(new AgentRuntime("t", 1, NullLog.Instance, diagnosticsEnabled: true));
        var e = Assert.Throws<SwAgentException>(() => op.Run(new DiagInterferenceRequest { DelayMs = ms }, new Ctx()));
        Assert.Equal("delay_ms", e.Error.Details!["field"]);
    }
}
