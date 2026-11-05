using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Server;

/// <summary>
/// What every swmcp tool shares: an input schema from a contract plus <c>deadline_ms</c>, a typed error returned as an
/// <c>isError</c> result whose text is <c>{code, message, details}</c> (so the agent can branch on <c>code</c>), and
/// one log line per call. Anything else is a server bug: logged, and reported as a protocol-level internal error.
/// </summary>
public abstract class SwTool : McpServerTool
{
    public const string DeadlineProperty = "deadline_ms";

    readonly ILogger _log;
    readonly Tool _tool;
    readonly string _label;

    protected SwTool(ToolDefinition definition, Type requestType, string description, string label, ILogger? log)
    {
        _log = log ?? NullLogger.Instance;
        _label = label;
        _tool = new Tool
        {
            Name = definition.Name,
            Description = description,
            InputSchema = ToElement(WithDeadline(JsonSchema.For(requestType))),
            Annotations = new ToolAnnotations { ReadOnlyHint = definition.ReadOnly, OpenWorldHint = false },
        };
    }

    public override Tool ProtocolTool => _tool;

    public override IReadOnlyList<object> Metadata => Array.Empty<object>();

    /// <summary>A request schema with the optional deadline_ms every tool takes.</summary>
    public static JsonObject WithDeadline(JsonObject schema)
    {
        var properties = schema.TryGetValue("properties", out var p) && p is JsonObject existing ? existing : new JsonObject();
        properties[DeadlineProperty] = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = $"How long this call may take, 1 to {OperationFamilies.MaxDeadlineMs} ms; the operation's default when absent.",
        };
        schema["properties"] = properties;
        return schema;
    }

    /// <summary>Runs the call with the arguments (deadline_ms removed) and the deadline, if one was given; returns a JSON tree.</summary>
    protected abstract Task<object?> RunAsync(JsonObject parameters, TimeSpan? deadline, CancellationToken cancellationToken);

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        string outcome = "ok";
        try
        {
            var parameters = Parameters(request.Params?.Arguments);
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("{Tool} params {Params}", _tool.Name, Json.Write(parameters));
            var deadline = TakeDeadline(parameters);
            object? result = await RunAsync(parameters, deadline, cancellationToken).ConfigureAwait(false);
            return Result(Json.Write(result, indented: true), isError: false);
        }
        catch (SwAgentException e)
        {
            outcome = WireNames.ForEnum(e.Error.Code);
            return Result(Json.Write(JsonCodec.ToJson(e.Error), indented: true), isError: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception e)
        {
            outcome = "internal_error";
            _log.LogError(e, "{Tool} ({Label}) failed with a server bug", _tool.Name, _label);
            throw new McpProtocolException($"internal error in {_tool.Name}; see the swmcp server log", e, McpErrorCode.InternalError);
        }
        finally
        {
            _log.LogInformation("{Tool}{Label}: {Outcome} in {DurationMs} ms", _tool.Name, _label.Length == 0 ? "" : " " + _label, outcome, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>Removes deadline_ms from the arguments; invalid_argument unless it is a whole number of milliseconds in range.</summary>
    static TimeSpan? TakeDeadline(JsonObject parameters)
    {
        if (!parameters.TryGetValue(DeadlineProperty, out var value)) return null;
        parameters.Remove(DeadlineProperty);
        if (value is not long ms)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{DeadlineProperty} must be a whole number of milliseconds",
                new JsonObject { ["field"] = DeadlineProperty });
        }
        OperationFamilies.ValidateOverride(ms);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>The MCP arguments as the pipe's JSON tree; none is an empty object.</summary>
    static JsonObject Parameters(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments == null || arguments.Count == 0) return new JsonObject();
        return (JsonObject)Json.Parse(JsonSerializer.Serialize(arguments))!;
    }

    static CallToolResult Result(string text, bool isError) => new CallToolResult
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = text } },
        IsError = isError,
    };

    static JsonElement ToElement(JsonObject schema)
    {
        using var document = JsonDocument.Parse(Json.Write(schema));
        return document.RootElement.Clone();
    }
}
