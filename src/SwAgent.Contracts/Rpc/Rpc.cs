using System;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Contracts.Rpc;

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int ServerError = -32000;
}

public sealed class RpcRequest
{
    public RpcRequest(long id, string method, JsonObject parameters, long deadlineMs)
    {
        Id = id;
        Method = method;
        Params = parameters;
        DeadlineMs = deadlineMs;
    }

    public long Id { get; }
    public string Method { get; }
    public JsonObject Params { get; }

    /// <summary>The budget in milliseconds, counted from the moment the add-in reads the request.</summary>
    public long DeadlineMs { get; }
}

public sealed class RpcResponse
{
    RpcResponse(long? id, object? result, int rpcCode, SwAgentError? error)
    {
        Id = id;
        Result = result;
        RpcCode = rpcCode;
        Error = error;
    }

    public long? Id { get; }
    public object? Result { get; }
    public int RpcCode { get; }
    public SwAgentError? Error { get; }
    public bool IsSuccess => Error == null;

    public static RpcResponse Success(long id, object? result) => new RpcResponse(id, result, 0, null);

    public static RpcResponse Failure(long? id, int rpcCode, SwAgentError error) => new RpcResponse(id, null, rpcCode, error);
}

/// <summary>A request line that could not be accepted; <see cref="Id"/> is set when the line had a readable id.</summary>
public sealed class RpcParseException : Exception
{
    public RpcParseException(long? id, int rpcCode, SwAgentError error) : base(error.Message)
    {
        Id = id;
        RpcCode = rpcCode;
        Error = error;
    }

    public long? Id { get; }
    public int RpcCode { get; }
    public SwAgentError Error { get; }
}

/// <summary>JSON-RPC 2.0, one message per line. The writer escapes newlines, so a message never spans lines.</summary>
public static class RpcCodec
{
    public static string FormatRequest(RpcRequest request) =>
        Json.Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.Id,
            ["method"] = request.Method,
            ["params"] = request.Params,
            ["deadline_ms"] = request.DeadlineMs,
        });

    public static string FormatResponse(RpcResponse response)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = response.Id };
        if (response.IsSuccess)
        {
            o["result"] = response.Result;
        }
        else
        {
            o["error"] = new JsonObject
            {
                ["code"] = (long)response.RpcCode,
                ["message"] = response.Error!.Message,
                ["data"] = JsonCodec.ToJson(response.Error),
            };
        }
        return Json.Write(o);
    }

    public static string Success(long id, object? result) => FormatResponse(RpcResponse.Success(id, result));

    public static string Failure(long? id, int rpcCode, SwAgentError error) => FormatResponse(RpcResponse.Failure(id, rpcCode, error));

    public static RpcRequest ParseRequest(string line)
    {
        object? tree;
        try
        {
            tree = Json.Parse(line);
        }
        catch (JsonParseException e)
        {
            throw new RpcParseException(null, RpcErrorCodes.ParseError,
                new SwAgentError(ErrorCode.InvalidArgument, "the request is not valid JSON: " + e.Message));
        }
        if (!(tree is JsonObject o)) throw Invalid(null, "the request must be a JSON object");
        long? id = o.TryGetValue("id", out var rawId) && rawId is long l ? l : (long?)null;
        if (id == null) throw Invalid(null, "the request needs an integer 'id'");
        foreach (var key in o.Keys)
        {
            if (key != "jsonrpc" && key != "id" && key != "method" && key != "params" && key != "deadline_ms")
                throw Invalid(id, $"unknown request member '{key}'");
        }
        if (!(o.TryGetValue("jsonrpc", out var version) && version is string v && v == "2.0"))
            throw Invalid(id, "'jsonrpc' must be \"2.0\"");
        if (!(o.TryGetValue("method", out var rawMethod) && rawMethod is string method && method.Length > 0))
            throw Invalid(id, "the request needs a 'method'");
        JsonObject parameters;
        if (!o.TryGetValue("params", out var rawParams) || rawParams == null) parameters = new JsonObject();
        else if (rawParams is JsonObject p) parameters = p;
        else throw Invalid(id, "'params' must be an object");
        if (!(o.TryGetValue("deadline_ms", out var rawDeadline) && rawDeadline is long deadline && deadline > 0))
            throw Invalid(id, "the request needs a positive integer 'deadline_ms'");
        if (deadline > int.MaxValue)
            throw Invalid(id, $"'deadline_ms' must be at most {int.MaxValue}");
        return new RpcRequest(id.Value, method, parameters, deadline);
    }

    public static RpcResponse ParseResponse(string line)
    {
        var o = Json.Parse(line) as JsonObject ?? throw new FormatException("a response must be a JSON object");
        long? id = o.TryGetValue("id", out var rawId) && rawId is long l ? l : (long?)null;
        if (o.TryGetValue("error", out var rawError) && rawError is JsonObject e)
        {
            int code = e.TryGetValue("code", out var c) && c is long cl ? (int)cl : RpcErrorCodes.ServerError;
            SwAgentError error = e.TryGetValue("data", out var data) && data is JsonObject
                ? JsonCodec.FromJson<SwAgentError>(data)
                : new SwAgentError(ErrorCode.OperationFailed,
                    e.TryGetValue("message", out var m) && m is string ms ? ms : "the add-in returned an error without a message");
            return RpcResponse.Failure(id, code, error);
        }
        if (!o.ContainsKey("result")) throw new FormatException("a response needs 'result' or 'error'");
        if (id == null) throw new FormatException("a successful response needs an integer 'id'");
        return RpcResponse.Success(id.Value, o["result"]);
    }

    static RpcParseException Invalid(long? id, string message) =>
        new RpcParseException(id, RpcErrorCodes.InvalidRequest, new SwAgentError(ErrorCode.InvalidArgument, message));
}
