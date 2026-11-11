using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SwAgent.Client;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

/// <summary>
/// The MCP server in process, configured exactly as <c>swmcp serve</c> configures it, and driven through the
/// SDK's own client over a pair of in-memory pipes.
/// </summary>
sealed class InProcessServer : IAsyncDisposable
{
    readonly CancellationTokenSource _stop;
    readonly McpServer _server;
    readonly Task _running;

    InProcessServer(Supervisor supervisor, CancellationTokenSource stop, McpServer server, Task running, McpClient client)
    {
        Supervisor = supervisor;
        _stop = stop;
        _server = server;
        _running = running;
        Client = client;
    }

    public Supervisor Supervisor { get; }

    public McpClient Client { get; }

    /// <summary>
    /// A server whose supervisor reaches SOLIDWORKS through <paramref name="options"/>. It never loads an add-in (a test
    /// that needs a load says so through <paramref name="attach"/>), and its SOLIDWORKS never exits unless the test
    /// says so through <paramref name="exits"/>.
    /// </summary>
    public static async Task<InProcessServer> StartAsync(ClientOptions options, ILogger? toolLog = null, FakeAttach? attach = null, FakeProcessWatch? exits = null, bool apiCall = true)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var supervisor = new Supervisor(new SupervisorOptions
        {
            Client = options,
            SolidWorks = attach ?? new FakeAttach((_, _) => throw new InvalidOperationException("this test must not load the add-in")),
            Registration = new FakeRegistration(),
            Processes = exits ?? new FakeProcessWatch(),
            PipeWait = TimeSpan.FromMilliseconds(300),
            ApiCall = apiCall,
        });
        var serverOptions = new McpServerOptions();
        McpHost.Configure(serverOptions, ToolCatalog.Create(supervisor, toolLog));
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), serverOptions);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return new InProcessServer(supervisor, stop, server, running, client);
    }

    /// <summary>Calls sw_status; returns whether it was an error result, and its JSON text.</summary>
    public Task<(bool IsError, JsonObject Body)> StatusAsync() => CallAsync("sw_status");

    /// <summary>Calls a tool; returns whether it was an error result, and its JSON text.</summary>
    public async Task<(bool IsError, JsonObject Body)> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await Client.CallToolAsync(tool, arguments ?? new Dictionary<string, object?>());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return (result.IsError == true, (JsonObject)Json.Parse(text)!);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
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
        Supervisor.Dispose();
        _stop.Dispose();
    }
}

/// <summary>A logger that keeps every entry, at every level, for the tests to read.</summary>
sealed class CapturingLogger : ILogger
{
    readonly object _lock = new object();
    readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new List<(LogLevel, string, Exception?)>();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_lock) return _entries.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) _entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>A process list that fails as a bug would: not with a typed error.</summary>
sealed class BrokenProcesses : IProcessList
{
    public IReadOnlyList<int> SolidWorksProcessIds() => throw new InvalidOperationException("process list exploded");
}
