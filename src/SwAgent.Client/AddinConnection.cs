using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

/// <summary>
/// One connection to an add-in's pipe: sends requests, matches responses by id, routes notifications, and turns
/// every transport failure into a typed error. Responses that arrive after their caller gave up are dropped.
/// </summary>
public sealed class AddinConnection : IDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly StreamReader _reader;
    readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<RpcResponse>>();
    readonly ConcurrentDictionary<long, byte> _abandoned = new ConcurrentDictionary<long, byte>();
    readonly ILogger _log;
    readonly Action<string, JsonObject>? _onNotification;
    readonly TimeSpan _grace;
    readonly string _pipeName;
    long _nextId;
    SwAgentError? _closed;
    Task _readLoop = Task.CompletedTask;

    AddinConnection(NamedPipeClientStream pipe, string pipeName, ClientOptions options)
    {
        _pipe = pipe;
        _pipeName = pipeName;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _log = options.Logger;
        _onNotification = options.OnNotification;
        _grace = options.ResponseGrace;
    }

    public bool IsOpen => Volatile.Read(ref _closed) == null;

    /// <summary>Connects to <c>\\.\pipe\&lt;pipeName&gt;</c> and checks that process <paramref name="expectedServerProcessId"/> serves it.</summary>
    public static async Task<AddinConnection> OpenAsync(string pipeName, int expectedServerProcessId, ClientOptions options, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)options.ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException || e is IOException || e is UnauthorizedAccessException)
        {
            pipe.Dispose();
            throw new SwAgentException(ErrorCode.AddinNotLoaded,
                $"could not connect to the add-in's pipe \\\\.\\pipe\\{pipeName} ({e.Message}); restart SOLIDWORKS if the add-in was unloaded",
                new JsonObject { ["pipe"] = pipeName });
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
        {
            int win32Error = Marshal.GetLastWin32Error();
            // The server may already have closed its end after saying why (busy): that line is still in the pipe.
            var refusal = await PendingRefusalAsync(pipe, RefusalWait).ConfigureAwait(false);
            pipe.Dispose();
            if (refusal != null) throw new SwAgentException(refusal);
            throw new SwAgentException(ErrorCode.AddinNotLoaded,
                $"could not tell which process serves \\\\.\\pipe\\{pipeName} (Win32 error {win32Error}); the add-in may have closed the connection",
                new JsonObject { ["pipe"] = pipeName, ["expected_process_id"] = (long)expectedServerProcessId, ["win32_error"] = (long)win32Error });
        }
        if (serverPid != (uint)expectedServerProcessId)
        {
            pipe.Dispose();
            throw new SwAgentException(ErrorCode.AddinNotLoaded,
                $"\\\\.\\pipe\\{pipeName} is served by process {serverPid}, not SOLIDWORKS process {expectedServerProcessId}; refusing to use it",
                new JsonObject { ["pipe"] = pipeName, ["server_process_id"] = (long)serverPid, ["expected_process_id"] = (long)expectedServerProcessId });
        }

        var connection = new AddinConnection(pipe, pipeName, options);
        connection._readLoop = Task.Run(connection.ReadLoopAsync);
        options.Logger.LogInformation("connected to \\\\.\\pipe\\{Pipe} (process {Pid})", pipeName, serverPid);
        return connection;
    }

    /// <summary>
    /// Sends one request and returns its result, or throws <see cref="SwAgentException"/> with the add-in's typed error,
    /// <c>timeout</c> when no answer came within the deadline (plus a short grace for transit), or
    /// <c>addin_not_loaded</c> when the connection broke.
    /// </summary>
    public async Task<object?> CallAsync(string method, JsonObject parameters, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        long deadlineMs = (long)Math.Ceiling(deadline.TotalMilliseconds);
        if (deadlineMs < 1 || deadlineMs > int.MaxValue)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"the deadline must be between 1 and {int.MaxValue} ms, not {deadlineMs}",
                new JsonObject { ["deadline_ms"] = deadlineMs });
        }
        ThrowIfClosed();
        long id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        if (!IsOpen && _pending.TryRemove(id, out _)) ThrowIfClosed();

        string line = RpcCodec.FormatRequest(new RpcRequest(id, method, parameters, deadlineMs));
        try
        {
            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException)
        {
            // The add-in may have said why before it closed (busy): let the reader deliver that first.
            _pending.TryRemove(id, out _);
            await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            Close(Disconnected($"sending {method} failed: {e.Message}"));
            ThrowIfClosed();
        }

        RpcResponse response;
        try
        {
            response = await reply.Task.WaitAsync(TimeSpan.FromMilliseconds(deadlineMs) + _grace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException || e is OperationCanceledException)
        {
            if (_pending.TryRemove(id, out _))
            {
                _abandoned[id] = 0;
                if (e is OperationCanceledException) throw;
                throw new SwAgentException(ErrorCode.Timeout,
                    $"{method} did not answer within its {deadlineMs} ms deadline; SOLIDWORKS may still be running it, so check sw_status before retrying",
                    new JsonObject { ["method"] = method, ["deadline_ms"] = deadlineMs, ["started"] = null });
            }
            response = await reply.Task.ConfigureAwait(false);
        }

        if (!response.IsSuccess) throw new SwAgentException(response.Error!);
        return response.Result;
    }

    public void Dispose()
    {
        Close(Disconnected("the connection was closed by this client"));
        _readLoop.Wait(TimeSpan.FromSeconds(2));
    }

    async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    async Task ReadLoopAsync()
    {
        SwAgentError? reason = null;
        try
        {
            while (reason == null)
            {
                string? line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (line.Length > 0) reason = Route(line);
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException)
        {
            _log.LogDebug("reading from \\\\.\\pipe\\{Pipe} stopped: {Message}", _pipeName, e.Message);
        }
        Close(reason ?? Disconnected("the add-in closed the connection"));
    }

    /// <summary>Delivers one line; returns an error when the line ends the connection.</summary>
    SwAgentError? Route(string line)
    {
        object? tree;
        try
        {
            tree = Json.Parse(line);
        }
        catch (JsonParseException e)
        {
            _log.LogWarning("ignored a line from the add-in that is not JSON: {Message}", e.Message);
            return null;
        }

        if (tree is JsonObject o && o.ContainsKey("method") && !o.ContainsKey("id"))
        {
            string method = o["method"] as string ?? "";
            var parameters = o.TryGetValue("params", out var p) && p is JsonObject po ? po : new JsonObject();
            try
            {
                _onNotification?.Invoke(method, parameters);
            }
            catch (Exception e)
            {
                _log.LogError(e, "the notification handler for {Method} failed", method);
            }
            return null;
        }

        RpcResponse response;
        try
        {
            response = RpcCodec.ParseResponse(line);
        }
        catch (Exception e)
        {
            _log.LogWarning("ignored a malformed response from the add-in: {Message}", e.Message);
            return null;
        }

        if (response.Id == null)
        {
            // Not an answer to any request: the add-in refusing the connection (busy) or failing it.
            return response.Error ?? Disconnected("the add-in sent a response without an id");
        }
        long id = response.Id.Value;
        if (_pending.TryRemove(id, out var reply)) reply.TrySetResult(response);
        else if (_abandoned.TryRemove(id, out _)) _log.LogInformation("dropped the late response to request {Id}", id);
        else _log.LogWarning("dropped a response to unknown request {Id}", id);
        return null;
    }

    void Close(SwAgentError reason)
    {
        if (Interlocked.CompareExchange(ref _closed, reason, null) != null) return;
        _log.LogInformation("connection to \\\\.\\pipe\\{Pipe} closed: {Reason}", _pipeName, reason.Message);
        try
        {
            _pipe.Dispose();
        }
        catch (Exception)
        {
            // Already broken.
        }
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var reply)) reply.TrySetException(new SwAgentException(reason));
        }
    }

    void ThrowIfClosed()
    {
        var closed = Volatile.Read(ref _closed);
        if (closed != null) throw new SwAgentException(closed);
    }

    /// <summary>How long a connection whose server end is gone is read for the reason it was refused.</summary>
    static readonly TimeSpan RefusalWait = TimeSpan.FromMilliseconds(500);

    /// <summary>The error in one pending id-less response (the add-in's refusal, such as busy), or null.</summary>
    static async Task<SwAgentError?> PendingRefusalAsync(NamedPipeClientStream pipe, TimeSpan wait)
    {
        try
        {
            using var timeout = new CancellationTokenSource(wait);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) return null;
            var response = RpcCodec.ParseResponse(line);
            return response.Id == null ? response.Error : null;
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException
            || e is FormatException || e is JsonParseException || e is SwAgentException)
        {
            return null;
        }
    }

    static SwAgentError Disconnected(string what) =>
        new SwAgentError(ErrorCode.AddinNotLoaded, $"{what}; SOLIDWORKS may have exited or unloaded the add-in");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
