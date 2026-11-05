using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SwAgent.Contracts;

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
}
