using System;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Server;

/// <summary>
/// The MCP server's one connection to the add-in: made on the first call, so the server starts cleanly without
/// SOLIDWORKS, and made again on the next call after it breaks. Calls go one at a time, as the add-in runs them.
/// </summary>
public sealed class AddinSession : IDisposable
{
    readonly ClientOptions _options;
    readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    SwAgentClient? _client;

    public AddinSession(ClientOptions options)
    {
        _options = options;
    }

    /// <summary>True while a connection is open; false before the first call and after the add-in dropped it.</summary>
    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>Runs one operation with the client's default deadline; throws <see cref="Contracts.SwAgentException"/> on any failure.</summary>
    public async Task<object?> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client == null || !_client.IsConnected)
            {
                _client?.Dispose();
                _client = null;
                _client = await SwAgentClient.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
            }
            return await _client.CallAsync(method, parameters, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
