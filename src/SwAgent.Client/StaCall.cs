using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

/// <summary>
/// Runs one out-of-process COM call to SOLIDWORKS on a dedicated STA thread, with a message filter that retries the
/// call while SOLIDWORKS rejects it as busy. Past <c>bound</c> the caller gets <c>busy</c> and the thread is abandoned
/// (a background thread, left to finish or hang on its own): a COM call cannot be safely aborted.
/// </summary>
public static class StaCall
{
    public static Task<T> RunAsync<T>(string what, Func<T> call, TimeSpan bound) => WithinAsync(Start(what, call, bound), what, bound);

    /// <summary>
    /// Starts the call on its STA thread and returns its own outcome, however long it takes: the message filter gives up
    /// retrying a busy SOLIDWORKS after <paramref name="filterBound"/>, but a call SOLIDWORKS accepted runs to its end.
    /// </summary>
    public static Task<T> Start<T>(string what, Func<T> call, TimeSpan filterBound)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            CoRegisterMessageFilter(new RetryRejectedCalls(filterBound), out var previous);
            try
            {
                done.TrySetResult(call());
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
            finally
            {
                CoRegisterMessageFilter(previous, out _);
            }
        })
        {
            IsBackground = true,
            Name = "swmcp-com: " + what,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    static async Task<T> WithinAsync<T>(Task<T> task, string what, TimeSpan bound)
    {
        try
        {
            return await task.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new SwAgentException(ErrorCode.Busy,
                $"SOLIDWORKS did not answer {what} within {bound.TotalSeconds:0.#} s; it is busy, so try again once it is idle",
                new JsonObject { ["operation"] = what, ["bound_ms"] = (long)bound.TotalMilliseconds, ["started"] = null });
        }
    }

    [DllImport("ole32.dll")]
    static extern int CoRegisterMessageFilter(IOleMessageFilter? filter, out IOleMessageFilter? previous);
}

/// <summary>The COM message filter interface (IMessageFilter in ole32).</summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

    [PreserveSig]
    int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
}

/// <summary>Retries a call SOLIDWORKS rejects as busy (SERVERCALL_RETRYLATER) every 100 ms until the bound; then gives up.</summary>
public sealed class RetryRejectedCalls : IOleMessageFilter
{
    public const int RetryAfterMs = 100;
    const int ServerCallIsHandled = 0;
    const int ServerCallRetryLater = 2;
    const int PendingMsgWaitDefProcess = 2;
    const int Cancel = -1;

    readonly TimeSpan _bound;

    public RetryRejectedCalls(TimeSpan bound)
    {
        _bound = bound;
    }

    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) => ServerCallIsHandled;

    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType) =>
        rejectType == ServerCallRetryLater && tickCount < _bound.TotalMilliseconds ? RetryAfterMs : Cancel;

    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) => PendingMsgWaitDefProcess;
}
