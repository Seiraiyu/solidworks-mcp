using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Mcp.Tests;

/// <summary>
/// A stand-in add-in in the test process: serves one pipe, answers session.hello itself, and hands every other
/// request to the test. The handler returns the response line, or null to stay silent.
/// </summary>
sealed class FakeAddin : IDisposable
{
    readonly NamedPipeServerStream _server;
    readonly Func<RpcRequest, string?> _handler;
    readonly Task _loop;
    readonly CancellationTokenSource _stop = new CancellationTokenSource();
    StreamWriter? _writer;

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

    /// <summary>Sends a line the client did not ask for: a notification or a late response.</summary>
    public async Task SendAsync(string line)
    {
        while (_writer == null) await Task.Delay(10);
        await _writer.WriteLineAsync(line);
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
                await _writer.WriteLineAsync(RpcCodec.Failure(null, RpcErrorCodes.ServerError,
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
                string? reply = request.Method == "session.hello"
                    ? RpcCodec.Success(request.Id, JsonCodec.ToJson(new SessionHelloResult
                    {
                        ProtocolVersion = ProtocolVersion,
                        AddinVersion = "0.1.0-fake",
                        SwRevision = "34.4.0",
                        ProcessId = Environment.ProcessId,
                    }))
                    : _handler(request);
                if (reply != null) await _writer.WriteLineAsync(reply);
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
