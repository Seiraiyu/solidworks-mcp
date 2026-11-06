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

/// <summary>
/// The tools the MCP server offers (design 371, MCP tools): the server tools (sw_status, sw_events, sw_reconnect), then
/// one tool per operation that is not internal, named <see cref="OperationDescriptor.ToolName"/>, read-only when its
/// contract says so. Nothing else decides the list.
/// </summary>
public static class ToolCatalog
{
    static readonly HashSet<string> ReadOnlyServerTools = new HashSet<string>(StringComparer.Ordinal) { "sw_status", "sw_events" };

    public static readonly IReadOnlyList<ToolDefinition> All = Build(ServerToolCatalog.All, OperationCatalog.All);

    /// <summary>The tools offered: every one, or every one but sw_api_call when the escape hatch is off.</summary>
    public static IReadOnlyList<ToolDefinition> For(bool apiCall) =>
        apiCall ? All : All.Where(t => t.Operation != Supervisor.ApiCallOperation).ToList();

    /// <summary>The tool list for these server tools and operations; internal operations are left out.</summary>
    public static IReadOnlyList<ToolDefinition> Build(IEnumerable<ServerToolDescriptor> serverTools, IEnumerable<OperationDescriptor> operations) =>
        serverTools.Select(t => new ToolDefinition(t.Name, null, ReadOnlyServerTools.Contains(t.Name)))
            .Concat(operations.Where(o => !o.Internal).Select(o => new ToolDefinition(o.ToolName, o.Name, o.ReadOnly)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A tool per definition the supervisor offers (sw_api_call only when its escape hatch is on), each built from its
    /// contract, logging its calls to <paramref name="log"/>.
    /// </summary>
    public static IReadOnlyList<McpServerTool> Create(Supervisor supervisor, ILogger? log = null) =>
        For(supervisor.ApiCallEnabled).Select(d => d.Operation != null
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
