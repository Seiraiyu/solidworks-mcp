using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class RequestExecutorTests
{
    static readonly DateTime Now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    static RpcResponse Run(OperationRegistry registry, RpcRequest request, DateTime? received = null, MemoryLog? log = null)
    {
        var executor = new RequestExecutor(registry, log ?? new MemoryLog(), () => Now);
        return RpcCodec.ParseResponse(executor.Execute(request, received ?? Now));
    }

    static RpcRequest Echo(string text, long deadlineMs = 1000) =>
        new RpcRequest(1, "test.echo", new JsonObject { ["text"] = text }, deadlineMs);

    [Fact]
    public void Runs_the_operation_and_returns_its_result()
    {
        var log = new MemoryLog();
        var response = Run(TestOps.Registry(), Echo("hi"), log: log);
        Assert.True(response.IsSuccess);
        Assert.Equal("hi", ((JsonObject)response.Result!)["text"]);
        Assert.True(log.Contains("request 1 test.echo deadline 1000 ms"));
        Assert.True(log.Contains(": ok"));
    }

    [Fact]
    public void Unknown_method_is_method_not_found()
    {
        var response = Run(TestOps.Registry(), new RpcRequest(2, "test.nope", new JsonObject(), 1000));
        Assert.Equal(RpcErrorCodes.MethodNotFound, response.RpcCode);
        Assert.Equal(ErrorCode.InvalidArgument, response.Error!.Code);
    }

    [Fact]
    public void Bad_params_are_invalid_params_naming_the_field()
    {
        var response = Run(TestOps.Registry(), new RpcRequest(3, "test.echo", new JsonObject(), 1000));
        Assert.Equal(RpcErrorCodes.InvalidParams, response.RpcCode);
        Assert.Equal("text", response.Error!.Details!["field"]);
    }

    [Fact]
    public void A_request_past_its_deadline_is_not_started_and_is_busy()
    {
        int runs = 0;
        var registry = TestOps.Registry(echo: (r, c) => { runs++; return new EchoResult { Text = r.Text }; });
        var response = Run(registry, Echo("late", 1000), received: Now.AddSeconds(-5));
        Assert.Equal(ErrorCode.Busy, response.Error!.Code);
        Assert.True(response.Error.Details!["started"] is false);
        Assert.Equal(5000L, response.Error.Details["waited_ms"]);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Execute_response_returns_the_typed_response()
    {
        var executor = new RequestExecutor(TestOps.Registry(), new MemoryLog(), () => Now);
        var response = executor.ExecuteResponse(Echo("hi"), Now);
        Assert.True(response.IsSuccess);
        Assert.Equal(1L, response.Id);
    }

    [Fact]
    public void A_given_start_time_decides_whether_the_deadline_had_passed()
    {
        // The clock says 5 s late, but the ledger started it 10 ms after receipt: it runs.
        var executor = new RequestExecutor(TestOps.Registry(), new MemoryLog(), () => Now.AddSeconds(5));
        var response = executor.ExecuteResponse(Echo("hi", 1000), Now, startedUtc: Now.AddMilliseconds(10));
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public void An_exception_from_run_is_operation_failed_and_logged()
    {
        var log = new MemoryLog();
        var registry = TestOps.Registry(echo: (r, c) => throw new InvalidOperationException("boom"));
        var response = Run(registry, Echo("x"), log: log);
        Assert.Equal(ErrorCode.OperationFailed, response.Error!.Code);
        Assert.Contains("test.echo failed: boom", response.Error.Message);
        Assert.Equal("System.InvalidOperationException", response.Error.Details!["exception"]);
        Assert.True(log.Contains("ERROR request 1 test.echo failed"));
    }

    [Fact]
    public void A_typed_error_from_run_keeps_its_code()
    {
        var registry = TestOps.Registry(echo: (r, c) => throw new SwAgentException(ErrorCode.WrongDocumentType, "not a part"));
        var response = Run(registry, Echo("x"));
        Assert.Equal(ErrorCode.WrongDocumentType, response.Error!.Code);
        Assert.Equal("not a part", response.Error.Message);
    }

    [Fact]
    public void An_exception_from_verify_is_verification_failed()
    {
        var registry = TestOps.Registry(verify: (r, res, c) => throw new InvalidOperationException("the feature is missing"));
        var response = Run(registry, Echo("x"));
        Assert.Equal(ErrorCode.VerificationFailed, response.Error!.Code);
        Assert.Contains("the feature is missing", response.Error.Message);
    }

    [Fact]
    public void The_context_carries_the_deadline()
    {
        DateTime seen = default;
        var registry = TestOps.Registry(echo: (r, c) => { seen = c.DeadlineUtc; return new EchoResult { Text = r.Text }; });
        Run(registry, Echo("x", 2500), received: Now.AddMilliseconds(-500));
        Assert.Equal(Now.AddMilliseconds(2000), seen);
    }
}
