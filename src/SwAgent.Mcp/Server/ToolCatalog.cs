using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SwAgent.Contracts;

namespace SwAgent.Mcp.Server;

/// <summary>One MCP tool: its name (underscores, as MCP clients require) and the operation it runs.</summary>
public sealed record ToolDefinition(string Name, string Operation, bool ReadOnly);

/// <summary>The tools the MCP server offers. Phase 1: sw_status. session.hello stays internal to the client.</summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All = new[]
    {
        new ToolDefinition("sw_status", "session.status", ReadOnly: true),
    };

    /// <summary>A tool per definition, each built from its operation's contract, logging its calls to <paramref name="log"/>.</summary>
    public static IReadOnlyList<McpServerTool> Create(AddinSession session, ILogger? log = null) =>
        All.Select(d => (McpServerTool)new ContractTool(d,
                OperationCatalog.Find(d.Operation) ?? throw new InvalidOperationException($"tool {d.Name} names unknown operation {d.Operation}"),
                session,
                log))
            .ToList();
}
