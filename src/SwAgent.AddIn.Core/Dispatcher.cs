using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;

namespace SwAgent.AddIn.Core;

/// <summary>Where a response goes: the pipe connection the request came in on.</summary>
public interface IReplyChannel
{
    bool IsOpen { get; }

    /// <summary>Queues a line for sending. Never blocks and never throws.</summary>
    void Send(string line);
}

/// <summary>
/// Runs requests on SOLIDWORKS' UI thread, one at a time, through a message-only window created on that thread.
/// After each request control goes back to SOLIDWORKS' message loop: the next queued request is started by a
/// 10 ms timer, and Windows delivers WM_TIMER only after posted, input and paint messages, so SOLIDWORKS repaints
/// and handles input between requests. At most one start message is outstanding, so requests never nest.
/// Requests live in a <see cref="RequestLedger"/>; one the watchdog already answered still runs, and its outcome is
/// raised as operation.completed.
/// </summary>
public sealed class Dispatcher : IDisposable
{
    const int WmApp = 0x8000;
    const int WmRun = WmApp + 0x5A1;
    const int WmProbe = WmApp + 0x5A2;
    const int WmTimer = 0x0113;
    static readonly UIntPtr TimerId = new UIntPtr(0x5A1);
    const uint BacklogDelayMs = 10;

    readonly RequestExecutor _executor;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    readonly RequestLedger _ledger;
    readonly EventHub? _events;
    readonly ConcurrentQueue<(string Name, Action Action)> _actions = new ConcurrentQueue<(string, Action)>();
    readonly object _gate = new object();
    readonly MessageWindow _window;
    readonly IntPtr _hwnd;

    int _scheduled;
    bool _running;
    volatile bool _disposed;
    long _lastPumpTicks;

    /// <summary>Call on the UI thread: the window, and so every request, belongs to the calling thread.</summary>
    public Dispatcher(RequestExecutor executor, ILog log, Func<DateTime>? utcNow = null, RequestLedger? ledger = null, EventHub? events = null)
    {
        _executor = executor;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _ledger = ledger ?? new RequestLedger();
        _events = events;
        UiThreadId = Thread.CurrentThread.ManagedThreadId;
        _window = new MessageWindow(this);
        _hwnd = _window.Handle;
    }

    public int UiThreadId { get; }

    public RequestLedger Ledger => _ledger;

    /// <summary>Runs on the UI thread after every request (the document table refresh); its exceptions are logged.</summary>
    public Action? AfterEachRequest { get; set; }

    /// <summary>The operation running now, or null.</summary>
    public string? CurrentMethod => _ledger.Running?.Request.Method;

    /// <summary>When the running operation started, or null.</summary>
    public DateTime? CurrentStartedUtc => _ledger.Running?.StartedUtc;

    public int QueuedRequests => _ledger.QueuedCount;

    /// <summary>When the UI thread last ran this dispatcher's window procedure; null before it ever has.</summary>
    public DateTime? LastPumpedUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastPumpTicks);
            return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Queues a request; safe from any thread.</summary>
    public void Enqueue(RpcRequest request, DateTime receivedUtc, IReplyChannel reply)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _ledger.Enqueue(request, receivedUtc, reply);
                Schedule(afterIdle: false);
                return;
            }
        }
        reply.Send(Unloading(request.Id));
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread before the next request; safe from any thread.</summary>
    public void Post(string name, Action action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _actions.Enqueue((name, action));
            Schedule(afterIdle: false);
        }
    }

    /// <summary>Asks the UI thread to record that it pumped messages; safe from any thread.</summary>
    public void Probe()
    {
        if (!_disposed) PostMessage(_hwnd, WmProbe, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Call on the UI thread. Queued requests are answered addin_not_loaded.</summary>
    public void Dispose()
    {
        if (Thread.CurrentThread.ManagedThreadId != UiThreadId)
            throw new InvalidOperationException("the dispatcher must be disposed on the UI thread that created it");
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _ledger.TakeAllQueued()) entry.TryAnswer(Unloading(entry.Request.Id), "unloading");
        }
        KillTimer(_hwnd, TimerId);
        _window.DestroyHandle();
    }

    void Schedule(bool afterIdle)
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;
        bool ok = afterIdle
            ? SetTimer(_hwnd, TimerId, BacklogDelayMs, IntPtr.Zero) != UIntPtr.Zero
            : PostMessage(_hwnd, WmRun, IntPtr.Zero, IntPtr.Zero);
        if (!ok)
        {
            Volatile.Write(ref _scheduled, 0);
            _log.Error($"dispatcher: could not schedule the next request (Win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    void MarkPumped() => Interlocked.Exchange(ref _lastPumpTicks, _utcNow().Ticks);

    void OnRun()
    {
        if (_running) return; // defensive: a nested pump never takes work; the outer run reschedules
        if (_disposed) return;
        _running = true;
        try
        {
            while (_actions.TryDequeue(out var posted)) RunAction(posted.Name, posted.Action);
            var now = _utcNow();
            bool started = _ledger.TryStart(now, out var entry, out var skipped);
            foreach (var (late, ahead) in skipped) AnswerBusy(late, ahead, now);
            if (started && entry != null) RunOne(entry);
        }
        finally
        {
            _running = false;
            Volatile.Write(ref _scheduled, 0);
            if ((_ledger.QueuedCount > 0 || !_actions.IsEmpty) && !_disposed) Schedule(afterIdle: true);
        }
    }

    void RunAction(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Error($"dispatcher: posted action '{name}' failed", e);
        }
    }

    void RunOne(LedgerEntry entry)
    {
        try
        {
            if (!entry.Reply.IsOpen)
            {
                _log.Info($"request {entry.Request.Id} {entry.Request.Method} dropped: its client disconnected");
                return;
            }
            var started = entry.StartedUtc ?? _utcNow();
            var response = _executor.ExecuteResponse(entry.Request, entry.ReceivedUtc, started);
            if (!entry.TryAnswer(RpcCodec.FormatResponse(response), "result"))
            {
                _log.Info($"request {entry.Request.Id} {entry.Request.Method} finished after its client was answered {entry.AnsweredAs}");
                _events?.Publish("operation.completed", new OperationCompletedEvent
                {
                    Id = entry.Request.Id,
                    Method = entry.Request.Method,
                    Ok = response.IsSuccess,
                    Error = response.Error,
                    DurationMs = (long)(_utcNow() - started).TotalMilliseconds,
                    AnsweredAs = entry.AnsweredAs ?? "unknown",
                });
            }
        }
        catch (Exception e)
        {
            _log.Error($"dispatcher: request {entry.Request.Id} could not be answered", e);
        }
        finally
        {
            _ledger.Finish(entry, _utcNow());
            var hook = AfterEachRequest;
            if (hook != null) RunAction("after each request", hook);
        }
    }

    /// <summary>A request the ledger refused to start because its deadline had passed: busy, naming what ran before it.</summary>
    void AnswerBusy(LedgerEntry late, int ahead, DateTime now)
    {
        var error = DeadlineErrors.Busy(late, null, _ledger.LastFinished, ahead, now);
        if (late.TryAnswer(RpcCodec.Failure(late.Request.Id, RpcErrorCodes.ServerError, error), "busy"))
            _log.Info($"request {late.Request.Id} {late.Request.Method}: busy, not started after {(now - late.ReceivedUtc).TotalMilliseconds:0} ms");
    }

    static string Unloading(long id) =>
        RpcCodec.Failure(id, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.AddinNotLoaded,
            "the add-in is unloading: SOLIDWORKS is closing or the add-in was turned off"));

    sealed class MessageWindow : NativeWindow
    {
        static readonly IntPtr HwndMessage = new IntPtr(-3);
        readonly Dispatcher _owner;

        public MessageWindow(Dispatcher owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        protected override void WndProc(ref Message m)
        {
            bool run = m.Msg == WmRun || (m.Msg == WmTimer && m.WParam == (IntPtr)(long)TimerId.ToUInt64());
            if (run || m.Msg == WmProbe)
            {
                try
                {
                    _owner.MarkPumped();
                    if (run)
                    {
                        if (m.Msg == WmTimer) KillTimer(m.HWnd, TimerId);
                        _owner.OnRun();
                    }
                }
                catch (Exception e)
                {
                    // Nothing may escape into SOLIDWORKS' message loop.
                    _owner._log.Error("dispatcher: unexpected failure in the window procedure", e);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapseMs, IntPtr timerProc);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
}
