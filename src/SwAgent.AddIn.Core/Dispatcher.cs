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
/// </summary>
public sealed class Dispatcher : IDisposable
{
    const int WmApp = 0x8000;
    const int WmRun = WmApp + 0x5A1;
    const int WmTimer = 0x0113;
    static readonly UIntPtr TimerId = new UIntPtr(0x5A1);
    const uint BacklogDelayMs = 10;

    readonly RequestExecutor _executor;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
    readonly object _gate = new object();
    readonly MessageWindow _window;
    readonly IntPtr _hwnd;

    int _scheduled;
    bool _running;
    volatile bool _disposed;
    volatile string? _currentMethod;
    long _currentStartedTicks;

    /// <summary>Call on the UI thread: the window, and so every request, belongs to the calling thread.</summary>
    public Dispatcher(RequestExecutor executor, ILog log, Func<DateTime>? utcNow = null)
    {
        _executor = executor;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        UiThreadId = Thread.CurrentThread.ManagedThreadId;
        _window = new MessageWindow(this);
        _hwnd = _window.Handle;
    }

    public int UiThreadId { get; }

    /// <summary>The operation running now, or null.</summary>
    public string? CurrentMethod => _currentMethod;

    /// <summary>When the running operation started, or null.</summary>
    public DateTime? CurrentStartedUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _currentStartedTicks);
            return ticks == 0 ? (DateTime?)null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public int QueuedRequests => _queue.Count;

    /// <summary>Queues a request; safe from any thread.</summary>
    public void Enqueue(RpcRequest request, DateTime receivedUtc, IReplyChannel reply)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _queue.Enqueue(new WorkItem(request, receivedUtc, reply));
                Schedule(afterIdle: false);
                return;
            }
        }
        reply.Send(Unloading(request.Id));
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
            while (_queue.TryDequeue(out var item)) item.Reply.Send(Unloading(item.Request.Id));
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

    void OnRun()
    {
        if (_running) return; // defensive: a nested pump never takes a request; the outer run reschedules
        if (_disposed) return;
        _running = true;
        try
        {
            if (_queue.TryDequeue(out var item)) RunOne(item);
        }
        finally
        {
            _running = false;
            Volatile.Write(ref _scheduled, 0);
            if (!_queue.IsEmpty && !_disposed) Schedule(afterIdle: true);
        }
    }

    void RunOne(WorkItem item)
    {
        if (!item.Reply.IsOpen)
        {
            _log.Info($"request {item.Request.Id} {item.Request.Method} dropped: its client disconnected");
            return;
        }
        _currentMethod = item.Request.Method;
        Interlocked.Exchange(ref _currentStartedTicks, _utcNow().Ticks);
        try
        {
            item.Reply.Send(_executor.Execute(item.Request, item.ReceivedUtc));
        }
        catch (Exception e)
        {
            _log.Error($"dispatcher: request {item.Request.Id} could not be answered", e);
        }
        finally
        {
            _currentMethod = null;
            Interlocked.Exchange(ref _currentStartedTicks, 0);
        }
    }

    static string Unloading(long id) =>
        RpcCodec.Failure(id, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.AddinNotLoaded,
            "the add-in is unloading: SOLIDWORKS is closing or the add-in was turned off"));

    sealed class WorkItem
    {
        public WorkItem(RpcRequest request, DateTime receivedUtc, IReplyChannel reply)
        {
            Request = request;
            ReceivedUtc = receivedUtc;
            Reply = reply;
        }

        public RpcRequest Request { get; }
        public DateTime ReceivedUtc { get; }
        public IReplyChannel Reply { get; }
    }

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
            if (m.Msg == WmRun || (m.Msg == WmTimer && m.WParam == (IntPtr)(long)TimerId.ToUInt64()))
            {
                if (m.Msg == WmTimer) KillTimer(m.HWnd, TimerId);
                try
                {
                    _owner.OnRun();
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
