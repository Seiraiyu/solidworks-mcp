using System;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core.Pipe;

/// <summary>
/// Serves <c>\\.\pipe\&lt;name&gt;</c> to one client at a time. A listening instance always exists, so the name is
/// never free for another process to take; a client arriving while another is connected is answered busy and closed.
/// Nothing here touches the SOLIDWORKS API.
/// </summary>
public sealed class PipeServer : IDisposable
{
    public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;
    const int MaxInstances = 4;

    readonly string _name;
    readonly Action<string, IReplyChannel> _onLine;
    readonly ILog _log;
    readonly int _maxBytes;
    readonly object _gate = new object();
    volatile bool _stopping;
    NamedPipeServerStream? _listening;
    PipeSession? _active;
    Task _acceptLoop = Task.CompletedTask;

    readonly Action<IReplyChannel>? _onConnected;
    readonly Action<IReplyChannel>? _onDisconnected;

    /// <param name="onLine">Called on a pool thread for every non-empty line, with the connection to answer on.</param>
    /// <param name="onConnected">Called before a client's first line is read.</param>
    /// <param name="onDisconnected">Called once when that client's connection closes, for any reason.</param>
    public PipeServer(string name, Action<string, IReplyChannel> onLine, ILog log, int maxMessageBytes = DefaultMaxMessageBytes,
        Action<IReplyChannel>? onConnected = null, Action<IReplyChannel>? onDisconnected = null)
    {
        _name = name;
        _onLine = onLine;
        _log = log;
        _maxBytes = maxMessageBytes;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
    }

    public string Name => _name;

    /// <summary>Creates the first instance; throws <see cref="PipeNameInUseException"/> if the name is taken.</summary>
    public void Start()
    {
        _listening = NativePipe.Create(_name, true, MaxInstances);
        _log.Info($"pipe: listening on \\\\.\\pipe\\{_name}");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        PipeSession? active;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _listening?.Dispose();
            _listening = null;
            active = _active;
        }
        active?.Shutdown(TimeSpan.FromSeconds(1));
        _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        _log.Info($"pipe: stopped {_name}");
    }

    async Task AcceptLoopAsync()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? current;
            lock (_gate) current = _listening;
            if (current == null) return;
            try
            {
                await current.WaitForConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                current.Dispose();
                if (_stopping) return;
                _log.Error("pipe: waiting for a client failed", e);
                if (!TryOpenNextInstance()) return;
                continue;
            }
            bool haveNext = TryOpenNextInstance();
            Handle(current);
            if (!haveNext) return;
        }
    }

    bool TryOpenNextInstance()
    {
        try
        {
            var next = NativePipe.Create(_name, false, MaxInstances);
            lock (_gate)
            {
                if (_stopping)
                {
                    next.Dispose();
                    return false;
                }
                _listening = next;
            }
            return true;
        }
        catch (Exception e)
        {
            _log.Error("pipe: could not open the next pipe instance; no further clients will be accepted", e);
            lock (_gate) _listening = null;
            return false;
        }
    }

    void Handle(NamedPipeServerStream stream)
    {
        int clientPid = NativePipe.ClientProcessId(stream);
        PipeSession? session = null;
        PipeSession? holder;
        lock (_gate)
        {
            holder = _active != null && _active.IsOpen ? _active : null;
            if (holder == null && !_stopping)
            {
                session = new PipeSession(stream, clientPid, _onLine, _log, _maxBytes, _onDisconnected);
                _active = session;
            }
        }
        if (session != null)
        {
            _log.Info($"pipe: client {clientPid} connected");
            try
            {
                _onConnected?.Invoke(session);
            }
            catch (Exception e)
            {
                _log.Error("pipe: the connect handler threw", e);
            }
            session.Start();
            return;
        }
        _ = RejectAsync(stream, clientPid, holder);
    }

    async Task RejectAsync(NamedPipeServerStream stream, int clientPid, PipeSession? holder)
    {
        try
        {
            if (holder == null) return; // stopping
            _log.Warn($"pipe: refused client {clientPid}; client {holder.ClientProcessId} is connected");
            string line = RpcCodec.Failure(null, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.Busy,
                $"another client (process {holder.ClientProcessId}) is connected to this SOLIDWORKS; only one agent can drive it at a time",
                new JsonObject { ["holder_process_id"] = (long)holder.ClientProcessId }));
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Warn($"pipe: could not tell client {clientPid} it was refused: {e.Message}");
        }
        finally
        {
            stream.Dispose();
        }
    }
}
