using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SwAgent.Client;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Server;

namespace SwAgent.Live.Tests;

/// <summary>
/// The MCP server configured exactly as swmcp serve configures it (McpHost.Configure with ToolCatalog.Create), over the
/// run's own supervisor, and the SDK's own client talking to it through in-memory pipes: tool calls as an agent makes
/// them, on the run's one connection to the add-in, which serves one client at a time. Disposing it leaves the
/// supervisor to the run.
/// </summary>
sealed class McpSession : IAsyncDisposable
{
    readonly CancellationTokenSource _stop;
    readonly McpServer _server;
    readonly Task _running;
    readonly McpClient _client;

    McpSession(CancellationTokenSource stop, McpServer server, Task running, McpClient client)
    {
        _stop = stop;
        _server = server;
        _running = running;
        _client = client;
    }

    public static async Task<McpSession> StartAsync(Supervisor supervisor)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new McpServerOptions();
        McpHost.Configure(options, ToolCatalog.Create(supervisor));
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return new McpSession(stop, server, running, client);
    }

    /// <summary>Calls a tool; whether it answered an error result, and its JSON body.</summary>
    public async Task<(bool IsError, JsonObject Body)> CallAsync(string tool, JsonObject arguments)
    {
        // The SDK serializes arguments with System.Text.Json, which would write our JsonObject as a list of pairs.
        var args = new Dictionary<string, object?>();
        foreach (var kv in arguments)
        {
            using var document = JsonDocument.Parse(Json.Write(kv.Value));
            args[kv.Key] = document.RootElement.Clone();
        }
        var result = await _client.CallToolAsync(tool, args).ConfigureAwait(false);
        if (result.Content.Count != 1 || result.Content[0] is not TextContentBlock block)
            throw new InvalidOperationException($"{tool} answered {result.Content.Count} content blocks, not one text block");
        return (result.IsError == true, (JsonObject)Json.Parse(block.Text)!);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _stop.Cancel();
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Stopped, as asked.
        }
        await _server.DisposeAsync();
        _stop.Dispose();
    }
}
