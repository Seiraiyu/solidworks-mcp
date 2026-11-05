using System;
using System.Linq;
using SwAgent.AddIn.Core.Pipe;
using SwAgent.Contracts.Rpc;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The add-in's runtime: a dispatcher on the UI thread fed by the pipe server. Start it in ConnectToSW and
/// dispose it in DisconnectFromSW, both on SOLIDWORKS' UI thread.
/// </summary>
public sealed class AgentHost : IDisposable
{
    readonly PipeServer _pipe;
    readonly ILog _log;
    bool _disposed;

    AgentHost(Dispatcher dispatcher, PipeServer pipe, ILog log)
    {
        Dispatcher = dispatcher;
        _pipe = pipe;
        _log = log;
    }

    public Dispatcher Dispatcher { get; }

    public string PipeName => _pipe.Name;

    /// <summary>Call on the UI thread. Throws (having released everything it made) if the pipe cannot be served.</summary>
    public static AgentHost Start(string pipeName, OperationRegistry registry, ILog log, Func<DateTime>? utcNow = null)
    {
        var clock = utcNow ?? (() => DateTime.UtcNow);
        var dispatcher = new Dispatcher(new RequestExecutor(registry, log, clock), log, clock);
        var pipe = new PipeServer(pipeName, (line, reply) => OnLine(line, reply, dispatcher, clock), log);
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
        log.Info($"agent host started on {pipeName}; operations: {string.Join(", ", registry.Names.OrderBy(n => n, StringComparer.Ordinal))}");
        return new AgentHost(dispatcher, pipe, log);
    }

    /// <summary>Call on the UI thread. Queued requests are answered addin_not_loaded before the pipe closes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispatcher.Dispose();
        _pipe.Dispose();
        _log.Info("agent host stopped");
    }

    static void OnLine(string line, IReplyChannel reply, Dispatcher dispatcher, Func<DateTime> clock)
    {
        var received = clock();
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
        dispatcher.Enqueue(request, received, reply);
    }
}
