using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Watches for SOLIDWORKS' modal dialogs from a background thread (never the UI thread), every 200 ms, through Win32
/// only. Each dialog gets an id that is never reused, and raises dialog.opened and dialog.closed. A dialog that opens
/// while a request runs answers that request dialog_open (the request goes on waiting on the dialog; its outcome
/// follows as operation.completed). A dialog the allow-list knows is answered at once. Nothing else is ever pressed
/// except through <see cref="Respond"/>, on the agent's request.
/// </summary>
public sealed class DialogWatch : IDisposable
{
    readonly IDialogWindows _windows;
    readonly RequestLedger _ledger;
    readonly EventHub _events;
    readonly ILog _log;
    readonly Func<DateTime> _utcNow;
    readonly DialogAllowList _allowList;
    readonly TimeSpan _period;
    readonly object _gate = new object();
    readonly object _tickGate = new object();
    readonly Dictionary<IntPtr, Tracked> _open = new Dictionary<IntPtr, Tracked>();
    readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
    long _counter;
    Thread? _thread;

    public DialogWatch(IDialogWindows windows, RequestLedger ledger, EventHub events, ILog log, Func<DateTime>? utcNow = null,
        DialogAllowList? allowList = null, TimeSpan? period = null)
    {
        _windows = windows;
        _ledger = ledger;
        _events = events;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _allowList = allowList ?? DialogAllowList.Default;
        _period = period ?? TimeSpan.FromMilliseconds(200);
    }

    public bool IsAlive => _thread?.IsAlive == true;

    /// <summary>
    /// The open dialog seen most recently, or null; read from any thread. A dialog whose window has gone is skipped at once,
    /// before the next pass notices it closed, so nothing is refused for a dialog that no longer exists.
    /// </summary>
    public DialogInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _open.Values
                    .Where(t => _windows.IsOpen(t.Native.Hwnd))
                    .OrderByDescending(t => t.Sequence)
                    .Select(t => t.Info)
                    .FirstOrDefault();
            }
        }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "swagent-dialog-watch" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>One pass: dialogs that closed, then dialogs that opened.</summary>
    public void Tick()
    {
        lock (_tickGate)
        {
            var now = _utcNow();
            var found = new HashSet<IntPtr>(_windows.FindModal());
            List<Tracked> closed;
            lock (_gate)
            {
                closed = _open.Values.Where(t => !found.Contains(t.Native.Hwnd)).ToList();
                foreach (var t in closed) _open.Remove(t.Native.Hwnd);
            }
            foreach (var t in closed)
            {
                _log.Info($"dialogs: {t.Info.Id} \"{t.Info.Title}\" closed");
                _events.Publish("dialog.closed", new DialogEvent { Dialog = t.Info });
            }
            foreach (var hwnd in found)
            {
                bool known;
                lock (_gate) known = _open.ContainsKey(hwnd);
                if (known) continue;
                var native = _windows.Read(hwnd);
                if (native != null) Opened(native, now);
            }
        }
    }

    void Opened(NativeDialog native, DateTime now)
    {
        var running = _ledger.Running;
        long sequence = Interlocked.Increment(ref _counter);
        var info = new DialogInfo
        {
            Id = string.Format(CultureInfo.InvariantCulture, "d{0}-{1:x}", sequence, native.Hwnd.ToInt64()),
            Title = native.Title,
            Text = native.Text.ToList(),
            Buttons = native.Buttons.Select(b => new DialogButton { Text = DialogButtons.Clean(b.Text), ControlId = b.ControlId }).ToList(),
            RequestId = running?.Request.Id,
            OpenedAt = now.ToString("o", CultureInfo.InvariantCulture),
        };
        lock (_gate) _open[native.Hwnd] = new Tracked(native, info, running, sequence);
        _log.Info($"dialogs: {info.Id} \"{info.Title}\" opened ({string.Join(" | ", info.Text)}), buttons [{string.Join(", ", info.Buttons.Select(b => b.Text))}]" +
                  (running == null ? ", no request running" : $", during request {running.Request.Id} {running.Request.Method}"));
        _events.Publish("dialog.opened", new DialogEvent { Dialog = info });

        var rule = _allowList.Match(native);
        if (rule != null)
        {
            var button = DialogButtons.Find(native, rule.Button)!;
            if (_windows.Press(native, button))
            {
                _log.Info($"dialogs: {info.Id} answered {DialogButtons.Clean(button.Text)} by the allow-list rule '{rule.Name}'");
                _events.Publish("dialog.answered", new DialogAnsweredEvent { Dialog = info.Id, Title = info.Title, Button = DialogButtons.Clean(button.Text), By = DialogAnsweredBy.AllowList });
                return;
            }
            // The dialog stays up: treat it as any other, so the request waiting on it is answered, not left to time out.
            _log.Warn($"dialogs: {info.Id} matched the allow-list rule '{rule.Name}' but pressing {DialogButtons.Clean(button.Text)} could not be posted");
        }
        if (running != null && !running.IsAnswered)
        {
            var error = DeadlineErrors.DialogOpen(running, info, started: true);
            if (running.TryAnswer(RpcCodec.Failure(running.Request.Id, RpcErrorCodes.ServerError, error), "dialog_open"))
                _log.Info($"request {running.Request.Id} {running.Request.Method}: dialog_open ({info.Id}); it goes on waiting on the dialog");
        }
    }

    void Loop()
    {
        while (!_stop.Wait(_period))
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // Never take SOLIDWORKS down from here; log and keep watching.
                _log.Error("dialogs: a pass failed", e);
            }
        }
    }

    sealed class Tracked
    {
        public Tracked(NativeDialog native, DialogInfo info, LedgerEntry? request, long sequence)
        {
            Native = native;
            Info = info;
            Request = request;
            Sequence = sequence;
        }

        public NativeDialog Native { get; }
        public DialogInfo Info { get; }
        public LedgerEntry? Request { get; }
        public long Sequence { get; }
    }
}
