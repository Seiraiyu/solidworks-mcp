using System;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DiagnosticOperationsTests
{
    sealed class Ctx : IOperationContext
    {
        public string Method => "diag.sleep";
        public DateTime DeadlineUtc => DateTime.UtcNow.AddMinutes(1);
        public ILog Log => NullLog.Instance;
    }

    [Fact]
    public void Sleep_is_refused_while_diagnostics_are_off()
    {
        IOperation op = new DiagSleepOperation(new AgentRuntime("t", 1, new MemoryLog()));
        var e = Assert.Throws<SwAgentException>(() => op.Run(new DiagSleepRequest { Ms = 10 }, new Ctx()));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains(@"HKCU\Software\solidworks-mcp", e.Message);
    }

    [Fact]
    public void Sleep_holds_the_calling_thread_when_diagnostics_are_on()
    {
        IOperation op = new DiagSleepOperation(new AgentRuntime("t", 1, new MemoryLog(), diagnosticsEnabled: true));
        var result = (DiagSleepResult)op.Run(new DiagSleepRequest { Ms = 50 }, new Ctx());
        Assert.True(result.SleptMs >= 50);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    public void Sleep_bounds_its_argument(int ms)
    {
        IOperation op = new DiagSleepOperation(new AgentRuntime("t", 1, new MemoryLog(), diagnosticsEnabled: true));
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => op.Run(new DiagSleepRequest { Ms = ms }, new Ctx())).Code);
    }
}
