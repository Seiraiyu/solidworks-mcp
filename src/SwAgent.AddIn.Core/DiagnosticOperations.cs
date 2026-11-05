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
