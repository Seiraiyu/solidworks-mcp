using System.Diagnostics;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>diag.sleep: holds the UI thread without pumping, so fault tests can make SOLIDWORKS busy on demand.</summary>
public sealed class DiagSleepOperation : Operation<DiagSleepRequest, DiagSleepResult>
{
    public const int MaxMs = 600_000;
    readonly AgentRuntime _runtime;

    public DiagSleepOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override DiagSleepResult Run(DiagSleepRequest request, IOperationContext context)
    {
        if (!_runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (request.Ms < 1 || request.Ms > MaxMs)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"ms must be between 1 and {MaxMs}, not {request.Ms}",
                new JsonObject { ["field"] = "ms" });
        }
        context.Log.Warn($"diag.sleep: holding the UI thread for {request.Ms} ms");
        var clock = Stopwatch.StartNew();
        Thread.Sleep(request.Ms);
        return new DiagSleepResult { SleptMs = clock.ElapsedMilliseconds };
    }

    protected override void Verify(DiagSleepRequest request, DiagSleepResult result, IOperationContext context)
    {
        if (result.SleptMs < request.Ms)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"slept {result.SleptMs} ms, less than the {request.Ms} ms asked");
    }
}

/// <summary>diag.interference: arms a hold inside the next interference session, and reports the sessions opened and closed (design 455's interference timeout fault case).</summary>
public sealed class DiagInterferenceOperation : Operation<DiagInterferenceRequest, DiagInterferenceResult>
{
    readonly AgentRuntime _runtime;

    public DiagInterferenceOperation(AgentRuntime runtime)
    {
        _runtime = runtime;
    }

    protected override DiagInterferenceResult Run(DiagInterferenceRequest request, IOperationContext context)
    {
        if (!_runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (request.DelayMs is int ms)
        {
            if (ms < 1 || ms > DiagSleepOperation.MaxMs)
                throw new SwAgentException(ErrorCode.InvalidArgument, $"delay_ms must be between 1 and {DiagSleepOperation.MaxMs}, not {ms}", new JsonObject { ["field"] = "delay_ms" });
            _runtime.ArmInterferenceDelay(ms);
            context.Log.Warn($"diag.interference: the next interference session is held open for {ms} ms");
        }
        return new DiagInterferenceResult { Opened = _runtime.InterferenceSessionsOpened, Closed = _runtime.InterferenceSessionsClosed, ArmedMs = _runtime.ArmedInterferenceDelay };
    }

    /// <summary>Nothing to check: the counters are read, the delay is taken by the next session.</summary>
    protected override void Verify(DiagInterferenceRequest request, DiagInterferenceResult result, IOperationContext context)
    {
    }
}
