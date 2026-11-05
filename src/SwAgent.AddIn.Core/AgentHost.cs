using System;
using System.Diagnostics;
using System.Linq;
using SwAgent.AddIn.Core.Pipe;
using SwAgent.Contracts.Rpc;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The add-in's runtime wired up: a dispatcher on the UI thread fed by the pipe server, a watchdog keeping deadlines,
/// pipe-thread operations answered at once, and events sent to the connected client. Start it in ConnectToSW and
/// dispose it in DisconnectFromSW, both on SOLIDWORKS' UI thread.
/// </summary>
public sealed class AgentHost : IDisposable
{
    readonly PipeServer _pipe;
    readonly Watchdog _watchdog;
    readonly ILog _log;
    bool _disposed;

    AgentHost(Dispatcher dispatcher, PipeServer pipe, Watchdog watchdog, AgentRuntime runtime)
    {
        Dispatcher = dispatcher;
        _pipe = pipe;
        _watchdog = watchdog;
        Runtime = runtime;
        _log = runtime.Log;
    }

    public Dispatcher Dispatcher { get; }

    public AgentRuntime Runtime { get; }

    public string PipeName => _pipe.Name;

    /// <summary>Runs on the UI thread, between requests, after a client disconnects (the preference restore).</summary>
    public Action? ClientDisconnected { get; set; }

    /// <summary>Phase 1 shape, for tests: a runtime of its own.</summary>
    public static AgentHost Start(string pipeName, OperationRegistry registry, ILog log, Func<DateTime>? utcNow = null) =>
        Start(pipeName, registry, new AgentRuntime("unknown", Process.GetCurrentProcess().Id, log, utcNow));

    /// <summary>Call on the UI thread. Throws (having released everything it made) if the pipe cannot be served.</summary>
    public static AgentHost Start(string pipeName, OperationRegistry registry, AgentRuntime runtime)
    {
        var log = runtime.Log;
        var executor = new RequestExecutor(registry, log, runtime.UtcNow);
        var dispatcher = new Dispatcher(executor, log, runtime.UtcNow, runtime.Ledger, runtime.Events);
        var watchdog = new Watchdog(runtime.Ledger, log, runtime.UtcNow, dispatcher.Probe);
        AgentHost? host = null;
        var pipe = new PipeServer(pipeName,
            (line, reply) => OnLine(line, reply, registry, executor, dispatcher, runtime),
            log,
            onConnected: client => runtime.Events.Attach(client),
            onDisconnected: client =>
            {
                runtime.Events.Detach(client);
                var action = host?.ClientDisconnected;
                if (action != null) dispatcher.Post("client disconnected", action);
            });
        try
        {
            pipe.Start();
        }
        catch
        {
            pipe.Dispose();
            dispatcher.Dispose();
            throw;
        }
        runtime.LastPumpedUtc = () => dispatcher.LastPumpedUtc;
        runtime.WatchdogAlive = () => watchdog.IsAlive;
        dispatcher.AfterEachRequest = () => runtime.Documents?.Refresh();
        watchdog.Start();
        host = new AgentHost(dispatcher, pipe, watchdog, runtime);
        log.Info($"agent host started on {pipeName}; operations: {string.Join(", ", registry.Names.OrderBy(n => n, StringComparer.Ordinal))}");
        return host;
    }

    /// <summary>Call on the UI thread. Queued requests are answered addin_not_loaded before the pipe closes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();
        Dispatcher.Dispose();
        _pipe.Dispose();
        _log.Info("agent host stopped");
    }

    static void OnLine(string line, IReplyChannel reply, OperationRegistry registry, RequestExecutor executor, Dispatcher dispatcher, AgentRuntime runtime)
    {
        var received = runtime.UtcNow();
        RpcRequest request;
        try
        {
            request = RpcCodec.ParseRequest(line);
        }
        catch (RpcParseException e)
        {
            reply.Send(RpcCodec.Failure(e.Id, e.RpcCode, e.Error));
            return;
        }
        if (registry.TryGet(request.Method, out var op) && op is IPipeOperation)
        {
            reply.Send(RpcCodec.FormatResponse(executor.ExecuteResponse(request, received)));
            return;
        }
        dispatcher.Enqueue(request, received, reply);
    }
}
