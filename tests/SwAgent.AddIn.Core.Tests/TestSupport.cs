using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;

namespace SwAgent.AddIn.Core.Tests;

sealed class MemoryLog : ILog
{
    public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();

    public bool IsDebugEnabled => true;
    public void Debug(string message) => Lines.Enqueue("DEBUG " + message);
    public void Info(string message) => Lines.Enqueue("INFO " + message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
    public void Error(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + (exception == null ? "" : " " + exception));

    public bool Contains(string text) => Lines.Any(l => l.Contains(text));

    /// <summary>Waits until a line containing <paramref name="text"/> has been logged.</summary>
    public void WaitFor(string text, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Contains(text))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"no log line containing '{text}'");
            Thread.Sleep(10);
        }
    }
}

static class TempDir
{
    public static string Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

[Operation("test.echo", typeof(EchoResult))]
public sealed class EchoRequest
{
    [Required] public string Text { get; set; } = "";
}

public sealed class EchoResult
{
    [Required] public string Text { get; set; } = "";
    public int ThreadId { get; set; }
}

[Operation("test.other", typeof(EchoResult))]
public sealed class OtherRequest
{
}

[Operation("test.peek", typeof(EchoResult), Thread = OperationThread.Pipe)]
public sealed class PeekRequest
{
}

/// <summary>A pipe-thread operation: answers on the reader thread.</summary>
public sealed class PeekOperation : PipeOperation<PeekRequest, EchoResult>
{
    protected override EchoResult Run(PeekRequest request, IOperationContext context) =>
        new EchoResult { Text = "peek", ThreadId = Thread.CurrentThread.ManagedThreadId };

    protected override void Verify(PeekRequest request, EchoResult result, IOperationContext context)
    {
    }
}

/// <summary>A UI-thread operation claiming a pipe contract: the registry must refuse it.</summary>
public sealed class WrongThreadPeekOperation : Operation<PeekRequest, EchoResult>
{
    protected override EchoResult Run(PeekRequest request, IOperationContext context) => new EchoResult();

    protected override void Verify(PeekRequest request, EchoResult result, IOperationContext context)
    {
    }
}

public sealed class OtherResult
{
}

/// <summary>An operation whose behaviour each test supplies.</summary>
public sealed class DelegateOperation<TRequest, TResult> : Operation<TRequest, TResult>
    where TRequest : class
    where TResult : class
{
    readonly Func<TRequest, IOperationContext, TResult> _run;
    readonly Action<TRequest, TResult, IOperationContext>? _verify;

    public DelegateOperation(Func<TRequest, IOperationContext, TResult> run, Action<TRequest, TResult, IOperationContext>? verify = null)
    {
        _run = run;
        _verify = verify;
    }

    protected override TResult Run(TRequest request, IOperationContext context) => _run(request, context);

    protected override void Verify(TRequest request, TResult result, IOperationContext context) => _verify?.Invoke(request, result, context);
}

public sealed class EchoOperation : Operation<EchoRequest, EchoResult>
{
    protected override EchoResult Run(EchoRequest request, IOperationContext context) =>
        new EchoResult { Text = request.Text, ThreadId = Thread.CurrentThread.ManagedThreadId };

    protected override void Verify(EchoRequest request, EchoResult result, IOperationContext context)
    {
    }
}

public sealed class OtherOperation : Operation<OtherRequest, EchoResult>
{
    protected override EchoResult Run(OtherRequest request, IOperationContext context) => new EchoResult { Text = "other" };

    protected override void Verify(OtherRequest request, EchoResult result, IOperationContext context)
    {
    }
}

public sealed class WrongResultOperation : Operation<OtherRequest, OtherResult>
{
    protected override OtherResult Run(OtherRequest request, IOperationContext context) => new OtherResult();

    protected override void Verify(OtherRequest request, OtherResult result, IOperationContext context)
    {
    }
}

static class TestOps
{
    public static readonly IReadOnlyList<OperationDescriptor> Contracts =
        OperationCatalog.FromTypes(new[] { typeof(EchoRequest), typeof(OtherRequest) });

    /// <summary>A registry whose test.echo runs <paramref name="echo"/> and whose test.other returns "other".</summary>
    public static OperationRegistry Registry(
        Func<EchoRequest, IOperationContext, EchoResult>? echo = null,
        Action<EchoRequest, EchoResult, IOperationContext>? verify = null,
        Func<OtherRequest, IOperationContext, EchoResult>? other = null) =>
        OperationRegistry.Create(Contracts, new IOperation[]
        {
            new DelegateOperation<EchoRequest, EchoResult>(
                echo ?? ((r, c) => new EchoResult { Text = r.Text, ThreadId = Thread.CurrentThread.ManagedThreadId }), verify),
            new DelegateOperation<OtherRequest, EchoResult>(other ?? ((r, c) => new EchoResult { Text = "other" })),
        });

    public static readonly IReadOnlyList<OperationDescriptor> ContractsWithPeek =
        OperationCatalog.FromTypes(new[] { typeof(EchoRequest), typeof(OtherRequest), typeof(PeekRequest) });

    /// <summary><see cref="Registry"/> plus the pipe-thread test.peek.</summary>
    public static OperationRegistry RegistryWithPeek(Func<EchoRequest, IOperationContext, EchoResult>? echo = null) =>
        OperationRegistry.Create(ContractsWithPeek, new IOperation[]
        {
            new DelegateOperation<EchoRequest, EchoResult>(
                echo ?? ((r, c) => new EchoResult { Text = r.Text, ThreadId = Thread.CurrentThread.ManagedThreadId })),
            new DelegateOperation<OtherRequest, EchoResult>((r, c) => new EchoResult { Text = "other" }),
            new PeekOperation(),
        });
}

/// <summary>An STA thread running a WinForms message loop: stands in for SOLIDWORKS' UI thread.</summary>
sealed class UiThread : IDisposable
{
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
    SynchronizationContext? _context;

    public UiThread()
    {
        _thread = new Thread(() =>
        {
            _context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_context);
            ThreadId = Thread.CurrentThread.ManagedThreadId;
            _ready.Set();
            Application.Run();
        }) { IsBackground = true, Name = "fake-ui" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("the fake UI thread did not start");
    }

    public int ThreadId { get; private set; }

    public SynchronizationContext Context => _context!;

    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        _context!.Send(_ => { try { result = func(); } catch (Exception e) { error = e; } }, null);
        if (error != null) throw new AggregateException(error);
        return result;
    }

    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    public void Dispose()
    {
        _context?.Post(_ => Application.ExitThread(), null);
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}

sealed class CapturingReply : IReplyChannel
{
    public volatile bool Open = true;
    public readonly BlockingCollection<string> Lines = new BlockingCollection<string>();

    public bool IsOpen => Open;

    public void Send(string line) => Lines.Add(line);

    public RpcResponse Next(int timeoutMs = 10000)
    {
        if (!Lines.TryTake(out var line, timeoutMs)) throw new TimeoutException("no reply");
        return RpcCodec.ParseResponse(line);
    }
}

static class Requests
{
    public static RpcRequest Echo(long id, string text, long deadlineMs = 10000) =>
        new RpcRequest(id, "test.echo", new Contracts.Serialization.JsonObject { ["text"] = text }, deadlineMs);

    public static RpcRequest Other(long id, long deadlineMs = 10000) =>
        new RpcRequest(id, "test.other", new Contracts.Serialization.JsonObject(), deadlineMs);
}

/// <summary>A bare line client, so pipe tests do not depend on SwAgent.Client (plan 1.B).</summary>
sealed class RawClient : IDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;

    public RawClient(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(5000);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public Task SendAsync(string line) => _writer.WriteLineAsync(line);

    public async Task<string?> ReadLineAsync(int timeoutMs = 10000)
    {
        var read = _reader.ReadLineAsync();
        if (await Task.WhenAny(read, Task.Delay(timeoutMs)) != read) throw new TimeoutException("no line from the pipe");
        return await read;
    }

    public void Dispose() => _pipe.Dispose();
}

static class PipeNames
{
    public static string Unique() => "swagent-test-" + Guid.NewGuid().ToString("N");
}
