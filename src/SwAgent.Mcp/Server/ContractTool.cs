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
/// An MCP tool made from an operation contract: its input schema is the contract's request schema, and a call is
/// forwarded to the add-in as that operation. A typed error comes back as an <c>isError</c> result whose text is
/// <c>{code, message, details}</c>, so the agent can branch on <c>code</c>. Anything else is a server bug: it is
/// logged and reported as a protocol-level internal error.
/// </summary>
public sealed class ContractTool : McpServerTool
{
    readonly OperationDescriptor _operation;
    readonly AddinSession _session;
    readonly ILogger _log;
    readonly Tool _tool;

    public ContractTool(ToolDefinition definition, OperationDescriptor operation, AddinSession session, ILogger? log = null)
    {
        _operation = operation;
        _session = session;
        _log = log ?? NullLogger.Instance;
        _tool = new Tool
        {
            Name = definition.Name,
            Description = operation.Description,
            InputSchema = ToElement(JsonSchema.For(operation.RequestType)),
            Annotations = new ToolAnnotations { ReadOnlyHint = definition.ReadOnly, OpenWorldHint = false },
        };
    }

    public override Tool ProtocolTool => _tool;

    public override IReadOnlyList<object> Metadata => Array.Empty<object>();

    /// <summary>The operation this tool runs on the pipe.</summary>
    public string Operation => _operation.Name;

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        string outcome = "ok";
        try
        {
            var parameters = Parameters(request.Params?.Arguments);
            if (_log.IsEnabled(LogLevel.Debug)) _log.LogDebug("{Tool} params {Params}", _tool.Name, Json.Write(parameters));
            object? result = await _session.CallAsync(_operation.Name, parameters, cancellationToken).ConfigureAwait(false);
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
            _log.LogError(e, "{Tool} ({Operation}) failed with a server bug", _tool.Name, _operation.Name);
            throw new McpProtocolException($"internal error in {_tool.Name}; see the swmcp server log", e, McpErrorCode.InternalError);
        }
        finally
        {
            _log.LogInformation("{Tool} {Operation}: {Outcome} in {DurationMs} ms", _tool.Name, _operation.Name, outcome, watch.ElapsedMilliseconds);
        }
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
