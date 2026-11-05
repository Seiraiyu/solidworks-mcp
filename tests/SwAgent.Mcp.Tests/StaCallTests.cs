using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class StaCallTests
{
    [Fact]
    public async Task The_call_runs_on_an_STA_thread_of_its_own()
    {
        int caller = Environment.CurrentManagedThreadId;
        var (apartment, thread) = await StaCall.RunAsync("probe", () => (Thread.CurrentThread.GetApartmentState(), Environment.CurrentManagedThreadId), TimeSpan.FromSeconds(10));
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(caller, thread);
    }

    [Fact]
    public async Task A_failure_reaches_the_caller()
    {
        var e = await Assert.ThrowsAsync<SwAgentException>(() =>
            StaCall.RunAsync<int>("load", () => throw new SwAgentException(ErrorCode.ApiError, "LoadAddIn failed"), TimeSpan.FromSeconds(10)));
        Assert.Equal(ErrorCode.ApiError, e.Code);
    }

    [Fact]
    public async Task A_call_past_its_bound_is_busy_and_abandoned()
    {
        using var gate = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<SwAgentException>(() =>
            StaCall.RunAsync("LoadAddIn", () => { gate.Wait(TimeSpan.FromSeconds(30)); return 0; }, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("LoadAddIn", e.Error.Details!["operation"]);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        gate.Set();
    }

    [Theory]
    [InlineData(0, 2, RetryRejectedCalls.RetryAfterMs)]     // busy: retry
    [InlineData(29_999, 2, RetryRejectedCalls.RetryAfterMs)]
    [InlineData(30_000, 2, -1)]                              // past the bound: give up
    [InlineData(0, 1, -1)]                                   // rejected outright: give up
    public void The_message_filter_retries_busy_calls_until_the_bound(int tickCount, int rejectType, int expected)
    {
        Assert.Equal(expected, new RetryRejectedCalls(TimeSpan.FromSeconds(30)).RetryRejectedCall(IntPtr.Zero, tickCount, rejectType));
    }
}
