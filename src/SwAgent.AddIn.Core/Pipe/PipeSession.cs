using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core.Pipe;

/// <summary>One connected client: a read loop handing lines to the server's handler, and a write loop draining replies.</summary>
internal sealed class PipeSession : IReplyChannel
{
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    readonly NamedPipeServerStream _stream;
    readonly Action<string, IReplyChannel> _onLine;
    readonly ILog _log;
    readonly int _maxBytes;
    readonly Action<PipeSession>? _onClosed;
    readonly ConcurrentQueue<string> _outgoing = new ConcurrentQueue<string>();
    readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    int _closed;
    volatile bool _closeWhenFlushed;
    Task _writer = Task.CompletedTask;

    public PipeSession(NamedPipeServerStream stream, int clientProcessId, Action<string, IReplyChannel> onLine, ILog log, int maxBytes, Action<PipeSession>? onClosed = null)
    {
        _stream = stream;
        ClientProcessId = clientProcessId;
        _onLine = onLine;
        _log = log;
        _maxBytes = maxBytes;
        _onClosed = onClosed;
    }

    public int ClientProcessId { get; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public void Start()
    {
        _writer = Task.Run(WriteLoopAsync);
        Task.Run(ReadLoopAsync);
    }

    public void Send(string line)
    {
        if (!IsOpen) return;
        _outgoing.Enqueue(line);
        _signal.Release();
    }

    /// <summary>Lets queued replies go out, waiting at most <paramref name="flushTimeout"/>, then closes.</summary>
    public void Shutdown(TimeSpan flushTimeout)
    {
        _closeWhenFlushed = true;
        _signal.Release();
        _writer.Wait(flushTimeout);
        Close();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        try { _stream.Dispose(); } catch (Exception) { /* already broken */ }
        _log.Info($"pipe: client {ClientProcessId} disconnected");
        try
        {
            _onClosed?.Invoke(this);
        }
        catch (Exception e)
        {
            _log.Error("pipe: the disconnect handler threw", e);
        }
    }

    async Task ReadLoopAsync()
    {
        var reader = new LineReader(_stream, _maxBytes);
        try
        {
            while (IsOpen)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                try
                {
                    _onLine(line, this);
                }
                catch (Exception e)
                {
                    _log.Error("pipe: the request handler threw", e);
                }
            }
        }
        catch (MessageTooLargeException e)
        {
            _log.Warn($"pipe: client {ClientProcessId} sent {e.Message}; closing the connection");
            Send(RpcCodec.Failure(null, RpcErrorCodes.InvalidRequest, new SwAgentError(ErrorCode.InvalidArgument,
                $"a message was longer than {_maxBytes} bytes; the connection was closed",
                new JsonObject { ["max_bytes"] = (long)_maxBytes })));
            _closeWhenFlushed = true;
            _signal.Release();
            return;
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException)
        {
            // The client went away.
        }
        catch (Exception e)
        {
            _log.Error("pipe: reading failed", e);
        }
        Close();
    }

    async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                await DrainAsync().ConfigureAwait(false);
                if (_closeWhenFlushed)
                {
                    await DrainAsync().ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException)
        {
            // The client went away.
        }
        catch (Exception e)
        {
            _log.Error("pipe: writing failed", e);
        }
        Close();
    }

    async Task DrainAsync()
    {
        bool wrote = false;
        while (_outgoing.TryDequeue(out var line))
        {
            byte[] bytes = Utf8.GetBytes(line + "\n");
            await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            wrote = true;
        }
        if (wrote) await _stream.FlushAsync().ConfigureAwait(false);
    }
}
