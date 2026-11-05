using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Runs one parsed request against the registry and returns the response line. Called on the UI thread by the
/// dispatcher; it never throws.
/// </summary>
public sealed class RequestExecutor
{
    readonly OperationRegistry _registry;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;

    public RequestExecutor(OperationRegistry registry, ILog log, Func<DateTime>? utcNow = null)
    {
        _registry = registry;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Execute(RpcRequest request, DateTime receivedUtc)
    {
        var started = _utcNow();
        RpcResponse response;
        try
        {
            response = ExecuteCore(request, receivedUtc, started);
        }
        catch (Exception e)
        {
            _log.Error($"request {request.Id} {request.Method}: unexpected failure", e);
            response = Fail(request, new SwAgentError(ErrorCode.OperationFailed, $"{request.Method} failed: {e.Message}"));
        }
        string outcome = response.IsSuccess ? "ok" : WireNames.ForEnum(response.Error!.Code);
        _log.Info($"request {request.Id} {request.Method} deadline {request.DeadlineMs} ms, " +
                  $"waited {(started - receivedUtc).TotalMilliseconds:0} ms, ran {(_utcNow() - started).TotalMilliseconds:0} ms: {outcome}");
        if (_log.IsDebugEnabled) _log.Debug($"request {request.Id} params {Json.Write(request.Params)}");
        return RpcCodec.FormatResponse(response);
    }

    RpcResponse ExecuteCore(RpcRequest request, DateTime receivedUtc, DateTime startedUtc)
    {
        if (!_registry.TryGet(request.Method, out var op) || op == null)
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.MethodNotFound,
                new SwAgentError(ErrorCode.InvalidArgument, $"unknown operation '{request.Method}'", new JsonObject { ["method"] = request.Method }));
        }

        var deadline = receivedUtc.AddMilliseconds(request.DeadlineMs);
        if (startedUtc >= deadline)
        {
            return Fail(request, new SwAgentError(ErrorCode.Timeout,
                $"{request.Method} was not started: its {request.DeadlineMs} ms deadline passed while SOLIDWORKS was busy with earlier requests; retry with a longer deadline",
                new JsonObject { ["started"] = false, ["waited_ms"] = (long)(startedUtc - receivedUtc).TotalMilliseconds }));
        }

        object typed;
        try
        {
            typed = JsonCodec.FromJson(request.Params, op.RequestType)!;
        }
        catch (SwAgentException e)
        {
            return RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidParams, e.Error);
        }

        var context = new OperationContext(request.Method, deadline, _log);

        object result;
        try
        {
            result = op.Run(typed, context);
        }
        catch (SwAgentException e)
        {
            _log.Warn($"request {request.Id} {request.Method}: {e.Error}");
            return Fail(request, e.Error);
        }
        catch (Exception e)
        {
            _log.Error($"request {request.Id} {request.Method} failed", e);
            return Fail(request, new SwAgentError(ErrorCode.OperationFailed, $"{request.Method} failed: {e.Message}",
                new JsonObject { ["exception"] = e.GetType().FullName }));
        }

        try
        {
            op.Verify(typed, result, context);
        }
        catch (SwAgentException e)
        {
            _log.Warn($"request {request.Id} {request.Method} verification: {e.Error}");
            return Fail(request, e.Error);
        }
        catch (Exception e)
        {
            _log.Error($"request {request.Id} {request.Method} verification failed", e);
            return Fail(request, new SwAgentError(ErrorCode.VerificationFailed,
                $"{request.Method} reported success but its check failed: {e.Message}",
                new JsonObject { ["exception"] = e.GetType().FullName }));
        }

        return RpcResponse.Success(request.Id, JsonCodec.ToJson(result));
    }

    static RpcResponse Fail(RpcRequest request, SwAgentError error) =>
        RpcResponse.Failure(request.Id, RpcErrorCodes.ServerError, error);
}
