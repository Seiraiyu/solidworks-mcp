using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Server;

/// <summary>
/// An MCP tool made from an operation contract: its input schema is the contract's request schema plus deadline_ms,
/// and a call is forwarded to the add-in as that operation, through the supervisor.
/// </summary>
public sealed class ContractTool : SwTool
{
    readonly OperationDescriptor _operation;
    readonly Supervisor _supervisor;

    public ContractTool(ToolDefinition definition, OperationDescriptor operation, Supervisor supervisor, ILogger? log = null)
        : base(definition, operation.RequestType, operation.Description, operation.Name, log)
    {
        _operation = operation;
        _supervisor = supervisor;
    }

    /// <summary>The operation this tool runs on the pipe.</summary>
    public string Operation => _operation.Name;

    protected override Task<object?> RunAsync(JsonObject parameters, TimeSpan? deadline, CancellationToken cancellationToken) =>
        _supervisor.CallAsync(_operation.Name, parameters, deadline, cancellationToken);
}

/// <summary>An MCP tool the server answers itself, from the supervisor: its schema is its server-tool contract plus deadline_ms.</summary>
public sealed class ServerTool : SwTool
{
    readonly ServerToolDescriptor _contract;
    readonly Func<object, TimeSpan?, CancellationToken, Task<object>> _run;

    public ServerTool(ToolDefinition definition, ServerToolDescriptor contract, Func<object, TimeSpan?, CancellationToken, Task<object>> run, ILogger? log = null)
        : base(definition, contract.RequestType, contract.Description, "", log)
    {
        _contract = contract;
        _run = run;
    }

    protected override async Task<object?> RunAsync(JsonObject parameters, TimeSpan? deadline, CancellationToken cancellationToken)
    {
        object request = JsonCodec.FromJson(parameters, _contract.RequestType)!;
        return JsonCodec.ToJson(await _run(request, deadline, cancellationToken).ConfigureAwait(false));
    }
}
