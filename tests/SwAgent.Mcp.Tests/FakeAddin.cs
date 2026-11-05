using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Tests;

/// <summary>
/// A stand-in add-in in the test process: serves one pipe, answers session.hello, addin.state (from <see cref="State"/>)
/// and addin.events (from <see cref="Held"/>) itself, and hands every other request to the test. The handler returns
/// the response line, or null to stay silent.
/// </summary>
sealed class FakeAddin : IDisposable
{
    readonly NamedPipeServerStream _server;
    readonly Func<RpcRequest, string?> _handler;
    readonly Task _loop;
    readonly CancellationTokenSource _stop = new CancellationTokenSource();
    readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    volatile StreamWriter? _writer;

    public FakeAddin(Func<RpcRequest, string?>? handler = null, int protocolVersion = Protocol.Version, bool busy = false)
    {
        PipeName = "swagent-test-" + Guid.NewGuid().ToString("N");
        ProtocolVersion = protocolVersion;
        Busy = busy;
        _handler = handler ?? (r => RpcCodec.Success(r.Id, JsonCodec.ToJson(SampleStatus())));
        _server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _loop = Task.Run(ServeAsync);
    }

    public string PipeName { get; }

    public int ProtocolVersion { get; }

    /// <summary>Answers the connection busy and closes it, as the add-in does for a second client.</summary>
    public bool Busy { get; }

    public BlockingCollection<RpcRequest> Received { get; } = new BlockingCollection<RpcRequest>();

    /// <summary>What addin.state answers; tests change it to show work running, a dialog, a stalled UI thread.</summary>
    public AddinStateResult State { get; set; } = new AddinStateResult
    {
        AddinVersion = "0.1.0-fake",
        ProtocolVersion = Protocol.Version,
        ProcessId = Environment.ProcessId,
        UiLastPumpedMs = 10,
        WatchdogAlive = true,
        DialogWatchAlive = true,
    };

    /// <summary>What addin.events answers: the events the add-in still holds.</summary>
    public List<EventEnvelope> Held { get; } = new List<EventEnvelope>();

    /// <summary>Options that reach this fake as "the only SOLIDWORKS", with short timeouts.</summary>
    public ClientOptions Options() => new ClientOptions
    {
        Processes = new FakeProcesses(Environment.ProcessId),
        Pipes = new FakePipes(name => name == PipeName),
        PipeNameFor = _ => PipeName,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ResponseGrace = TimeSpan.FromMilliseconds(100),
    };

    public RpcRequest Next(int timeoutMs = 10000) =>
        Received.TryTake(out var r, timeoutMs) ? r : throw new TimeoutException("the fake add-in received nothing");

    /// <summary>The next request for <paramref name="method"/>, skipping any other.</summary>
    public RpcRequest Next(string method, int timeoutMs = 10000)
    {
        while (true)
        {
            var r = Next(timeoutMs);
            if (r.Method == method) return r;
        }
    }

    /// <summary>Pushes an event, as the add-in does.</summary>
    public Task SendEventAsync(long seq, string kind, object data) =>
        SendAsync(Json.Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = EventEnvelope.Method,
            ["params"] = JsonCodec.ToJson(new EventEnvelope { Seq = seq, Kind = kind, Time = DateTime.UtcNow.ToString("o"), Data = (JsonObject)JsonCodec.ToJson(data)! }),
        }));

    /// <summary>Says session.closing and drops the client, as DisconnectFromSW does.</summary>
    public async Task CloseAsync(long seq)
    {
        await SendEventAsync(seq, "session.closing", new SessionClosingEvent { Reason = "SOLIDWORKS is closing" });
        await Task.Delay(50);
        Disconnect();
    }

    /// <summary>Sends a line the client did not ask for: a notification or a late response.</summary>
    public async Task SendAsync(string line)
    {
        while (_writer == null) await Task.Delay(10);
        await WriteAsync(line);
    }

    /// <summary>One write at a time: a test's SendAsync can race the serve loop's own replies on the same writer.</summary>
    async Task WriteAsync(string line)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer!.WriteLineAsync(line);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Drops the client, as SOLIDWORKS exiting would.</summary>
    public void Disconnect() => _server.Dispose();

    public static SessionStatusResult SampleStatus() => new SessionStatusResult
    {
        SwRevision = "34.4.0",
        ProcessId = Environment.ProcessId,
        AddinVersion = "0.1.0-fake",
        ProtocolVersion = Protocol.Version,
        ActiveDocument = new DocumentInfo { Title = "Part1", Type = DocumentType.Part, Dirty = true },
        OpenDocumentCount = 1,
    };

    async Task ServeAsync()
    {
        try
        {
            await _server.WaitForConnectionAsync(_stop.Token);
            using var reader = new StreamReader(_server, new UTF8Encoding(false));
            _writer = new StreamWriter(_server, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            if (Busy)
            {
                await WriteAsync(RpcCodec.Failure(null, RpcErrorCodes.ServerError,
                    new SwAgentError(ErrorCode.Busy, "another client (process 1) is connected", new JsonObject { ["holder_process_id"] = 1L })));
                _server.Dispose();
                return;
            }
            while (true)
            {
                string? line = await reader.ReadLineAsync(_stop.Token);
                if (line == null) return;
                var request = RpcCodec.ParseRequest(line);
                Received.Add(request);
                string? reply = request.Method switch
                {
                    "session.hello" => RpcCodec.Success(request.Id, JsonCodec.ToJson(new SessionHelloResult
                    {
                        ProtocolVersion = ProtocolVersion,
                        AddinVersion = "0.1.0-fake",
                        SwRevision = "34.4.0",
                        ProcessId = Environment.ProcessId,
                    })),
                    "addin.state" => RpcCodec.Success(request.Id, JsonCodec.ToJson(State)),
                    "addin.events" => RpcCodec.Success(request.Id, JsonCodec.ToJson(new AddinEventsResult
                    {
                        Events = Held.Where(e => e.Seq > ((request.Params.TryGetValue("after_seq", out var a) ? a as long? : null) ?? 0)).ToList(),
                        LastSeq = Held.Count == 0 ? 0 : Held[Held.Count - 1].Seq,
                    })),
                    _ => _handler(request),
                };
                if (reply != null) await WriteAsync(reply);
            }
        }
        catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException)
        {
            // The test is over or dropped the client.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Dispose();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
