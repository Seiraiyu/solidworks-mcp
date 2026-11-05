using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SwAgent.Client;
using SwAgent.Contracts;

namespace SwAgent.Mcp.Server;

/// <summary>
/// One MCP tool: its name (underscores, as MCP clients require), and the add-in operation it runs; a null operation
/// is a server tool, answered by the supervisor from the server-tool contract of the same name.
/// </summary>
public sealed record ToolDefinition(string Name, string? Operation, bool ReadOnly);

/// <summary>The tools the MCP server offers. Phase 2: sw_status, sw_dialog_respond, sw_events, sw_reconnect.</summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All = new[]
    {
        new ToolDefinition("sw_status", null, ReadOnly: true),
        new ToolDefinition("sw_dialog_respond", "dialog.respond", ReadOnly: false),
        new ToolDefinition("sw_events", null, ReadOnly: true),
        new ToolDefinition("sw_reconnect", null, ReadOnly: false),
    };

    /// <summary>A tool per definition, each built from its contract, logging its calls to <paramref name="log"/>.</summary>
    public static IReadOnlyList<McpServerTool> Create(Supervisor supervisor, ILogger? log = null) =>
        All.Select(d => d.Operation != null
                ? (McpServerTool)new ContractTool(d,
                    OperationCatalog.Find(d.Operation) ?? throw new InvalidOperationException($"tool {d.Name} names unknown operation {d.Operation}"),
                    supervisor, log)
                : new ServerTool(d,
                    ServerToolCatalog.Find(d.Name) ?? throw new InvalidOperationException($"tool {d.Name} has no server-tool contract"),
                    Handler(d.Name, supervisor), log))
            .ToList();

    static Func<object, TimeSpan?, CancellationToken, Task<object>> Handler(string name, Supervisor supervisor) => name switch
    {
        "sw_status" => async (_, deadline, ct) => await supervisor.StatusAsync(deadline, ct).ConfigureAwait(false),
        "sw_events" => (request, _, _) => Task.FromResult<object>(supervisor.ReadEvents((SwEventsRequest)request)),
        "sw_reconnect" => async (request, deadline, ct) => await supervisor.ReconnectAsync(((SwReconnectRequest)request).ProcessId, deadline, ct).ConfigureAwait(false),
        _ => throw new InvalidOperationException($"server tool {name} has no handler"),
    };
}
