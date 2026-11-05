using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

public sealed class SupervisorOptions
{
    /// <summary>How SOLIDWORKS is found and connected to; its ProcessId names the instance to use, null for the only one.</summary>
    public ClientOptions Client { get; set; } = new ClientOptions();

    public ISolidWorksAttach SolidWorks { get; set; } = ComSolidWorksAttach.Instance;

    /// <summary>Where the add-in is registered; required, since only swmcp knows how to read it.</summary>
    public IAddinRegistration? Registration { get; set; }

    /// <summary>How long the add-in's UI thread may go without pumping, while nothing runs, before SOLIDWORKS counts as not responding.</summary>
    public TimeSpan NotRespondingAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the add-in's pipe may take to appear after LoadAddIn.</summary>
    public TimeSpan PipeWait { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The one way swmcp reaches SOLIDWORKS (phase 2): finds the SOLIDWORKS to use, waits while it starts, loads the add-in
/// when it is not serving, keeps one multiplexed connection, keeps the events it receives, and tells a crash from a
/// close, an add-in turned off or an add-in that failed. Only connecting is serialized; calls run concurrently, so
/// sw_status answers while another call waits on SOLIDWORKS. It never starts, restarts or terminates SOLIDWORKS.
/// </summary>
public sealed class Supervisor : IDisposable
{
    readonly SupervisorOptions _options;
    readonly ILogger _log;
    readonly SemaphoreSlim _connect = new SemaphoreSlim(1, 1);
    readonly object _gate = new object();
    Link? _link;
    int? _pinned;
    SupervisorState _state = SupervisorState.NotRunning;
    DateTime? _hungSince;
    bool _disposed;

    public Supervisor(SupervisorOptions options)
    {
        _options = options;
        _log = options.Client.Logger;
        _pinned = options.Client.ProcessId;
        var registration = options.Registration ?? throw new ArgumentException("SupervisorOptions.Registration is required");
        Loader = new AddinLoader(options.SolidWorks, registration, options.Client.Pipes, options.Client.PipeNameFor, _log) { PipeWait = options.PipeWait };
    }

    public AddinLoader Loader { get; }

    public EventLog Events { get; } = new EventLog();

    /// <summary>Raised for each event as the add-in sent it (its own numbering), on the connection's reader thread.</summary>
    public event Action<EventEnvelope>? EventReceived;

    /// <summary>The state the supervisor last found.</summary>
    public SupervisorState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate) return _link != null && _link.Client.IsConnected;
        }
    }

    /// <summary>
    /// Runs one operation, connecting first (and loading the add-in) within its deadline; the add-in then gets the
    /// whole deadline.
    /// </summary>
    public async Task<object?> CallAsync(string method, JsonObject parameters, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        var limit = deadline ?? OperationFamilies.DefaultFor(method, _options.Client.DefaultDeadline);
        OperationFamilies.ValidateOverride((long)Math.Ceiling(limit.TotalMilliseconds));
        var link = await ConnectAsync(DateTime.UtcNow + limit, cancellationToken).ConfigureAwait(false);
        try
        {
            return await link.Client.CallAsync(method, parameters, limit, cancellationToken).ConfigureAwait(false);
        }
        catch (SwAgentException) when (!link.Client.IsConnected)
        {
            // The connection broke under the call: answer with what broke it, not a generic disconnect.
            throw new SwAgentException(await link.Classified.ConfigureAwait(false));
        }
    }

    /// <summary>A typed call through <see cref="CallAsync(string, JsonObject, TimeSpan?, CancellationToken)"/>.</summary>
    public async Task<TResult> CallAsync<TRequest, TResult>(TRequest request, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
        where TRequest : class
        where TResult : class
    {
        var op = typeof(TRequest).GetCustomAttribute<OperationAttribute>()
            ?? throw new ArgumentException($"{typeof(TRequest).Name} is not an operation request");
        object? tree = await CallAsync(op.Name, (JsonObject)JsonCodec.ToJson(request)!, deadline, cancellationToken).ConfigureAwait(false);
        return JsonCodec.FromJson<TResult>(tree);
    }

    /// <summary>The events received, after a cursor (sw_events).</summary>
    public SwEventsResult ReadEvents(SwEventsRequest request) => Events.Read(request.AfterSeq, request.Kinds, request.Limit);

    /// <summary>
    /// Drops the connection, forgets failed add-in loads, and uses process
    /// <paramref name="processId"/> (or the only SOLIDWORKS) from now on; answers with the status.
    /// </summary>
    public async Task<SwStatusResult> ReconnectAsync(int? processId, TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Link? old;
            lock (_gate)
            {
                old = _link;
                _link = null;
                _pinned = processId;
            }
            old?.Drop();
            foreach (int pid in _options.Client.Processes.SolidWorksProcessIds()) Loader.Forget(pid);
            if (processId != null) Loader.Forget(processId.Value);
            _log.LogInformation("reconnect: {Target}", processId == null ? "the only SOLIDWORKS" : $"SOLIDWORKS process {processId}");
        }
        finally
        {
            _connect.Release();
        }
        return await StatusAsync(deadline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What SOLIDWORKS is doing (sw_status). Always answers: when SOLIDWORKS is absent, starting, ambiguous or the add-in
    /// cannot be reached, that is the state, with the reason in error and what to do in message.
    /// </summary>
    public async Task<SwStatusResult> StatusAsync(TimeSpan? deadline = null, CancellationToken cancellationToken = default)
    {
        var limit = deadline ?? OperationFamilies.DefaultDeadline("session");
        var result = new SwStatusResult
        {
            SwmcpVersion = SwAgentClient.Version,
            ProcessIds = _options.Client.Processes.SolidWorksProcessIds().ToList(),
        };

        Link link;
        AddinStateResult addin;
        try
        {
            link = await ConnectAsync(DateTime.UtcNow + limit, cancellationToken).ConfigureAwait(false);
            addin = await link.Client.CallAsync<AddinStateRequest, AddinStateResult>(new AddinStateRequest(), OperationFamilies.DefaultDeadline("addin"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SwAgentException e)
        {
            var error = e.Error;
            Link? broken;
            lock (_gate) broken = _link;
            if (broken != null && !broken.Client.IsConnected) error = await broken.Classified.ConfigureAwait(false);
            return Unavailable(result, error);
        }

        result.State = SupervisorState.Ready;
        result.ProcessId = link.ProcessId;
        result.Addin = addin;

        bool stalled = addin.Running == null && addin.UiLastPumpedMs is long pumped && pumped > _options.NotRespondingAfter.TotalMilliseconds;
        bool hung = _options.SolidWorks.IsHung(link.ProcessId);
        var now = DateTime.UtcNow;
        lock (_gate) _hungSince = hung ? _hungSince ?? now : null;
        result.Responding = !(stalled || hung);
        if (stalled) result.NotRespondingMs = addin.UiLastPumpedMs;
        else if (hung) lock (_gate) result.NotRespondingMs = (long)(now - _hungSince!.Value).TotalMilliseconds;

        bool idle = result.Responding == true && addin.Running == null && addin.Queued.Count == 0 && addin.Dialog == null;
        if (idle)
        {
            try
            {
                result.Session = await link.Client.CallAsync<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), limit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SwAgentException e)
            {
                // Something started between the two questions; the add-in's state above still says what is going on.
                _log.LogDebug("sw_status: session.status skipped: {Message}", e.Message);
            }
        }
        result.Message = ReadyMessage(link.ProcessId, addin, result);
        return result;
    }

    public void Dispose()
    {
        Link? link;
        lock (_gate)
        {
            _disposed = true;
            link = _link;
            _link = null;
        }
        link?.Drop();
    }

    /// <summary>The live connection, made (discovering SOLIDWORKS and loading the add-in) when there is none.</summary>
    async Task<Link> ConnectAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Supervisor));
            if (_link != null && _link.Client.IsConnected) return _link;
        }
        var left = deadlineUtc - DateTime.UtcNow;
        if (!await _connect.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            // Another call is connecting (usually: waiting for the add-in to load); do not stall behind it.
            throw new SwAgentException(ErrorCode.Busy,
                "swmcp is still connecting to SOLIDWORKS (loading the add-in); nothing was run, so call again",
                new JsonObject { ["started"] = false, ["loading"] = State == SupervisorState.Loading });
        }
        try
        {
            int? pinned;
            lock (_gate)
            {
                if (_link != null && _link.Client.IsConnected) return _link;
                pinned = _pinned;
            }
            int pid;
            try
            {
                pid = new AddinLocator(_options.Client.Processes, _options.Client.Pipes, _options.Client.PipeNameFor).LocateProcess(pinned);
            }
            catch (SwAgentException e)
            {
                SetState(e.Code == ErrorCode.SwMultipleInstances ? SupervisorState.Multiple : SupervisorState.NotRunning, e.Message);
                throw;
            }

            try
            {
                if (!_options.Client.Pipes.Exists(_options.Client.PipeNameFor(pid))) SetState(SupervisorState.Loading, $"loading the add-in into SOLIDWORKS process {pid}");
                await Loader.EnsureLoadedAsync(pid, deadlineUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (SwAgentException e)
            {
                SetState(e.Code == ErrorCode.SwStarting ? SupervisorState.Starting : IsLoading(e.Error) ? SupervisorState.Loading : SupervisorState.AddinUnavailable, e.Message);
                throw;
            }

            var link = new Link(this, pid);
            try
            {
                var client = await SwAgentClient.ConnectAsync(ClientOptionsFor(pid, link), cancellationToken).ConfigureAwait(false);
                link.Start(client);
            }
            catch (SwAgentException e)
            {
                SetState(SupervisorState.AddinUnavailable, e.Message);
                throw;
            }
            await link.CatchUpAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) _link = link;
            SetState(SupervisorState.Ready, $"connected to SOLIDWORKS process {pid} (add-in {link.Client.Hello.AddinVersion})");
            return link;
        }
        finally
        {
            _connect.Release();
        }
    }

    ClientOptions ClientOptionsFor(int pid, Link link)
    {
        var o = _options.Client;
        return new ClientOptions
        {
            ProcessId = pid,
            ConnectTimeout = o.ConnectTimeout,
            HelloDeadline = o.HelloDeadline,
            DefaultDeadline = o.DefaultDeadline,
            ResponseGrace = o.ResponseGrace,
            Processes = o.Processes,
            Pipes = o.Pipes,
            PipeNameFor = o.PipeNameFor,
            Logger = o.Logger,
            OnNotification = link.OnNotification,
        };
    }

    void SetState(SupervisorState state, string why)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != state;
            _state = state;
        }
        if (changed) _log.LogInformation("supervisor: {State}: {Why}", WireNames.ForEnum(state), why);
    }

    /// <summary>Called once per link when its connection ends.</summary>
    void Ended(Link link, SwAgentError error)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_link, link)) _link = null;
        }
        SetState(error.Code == ErrorCode.SwNotRunning ? SupervisorState.NotRunning : SupervisorState.AddinUnavailable, error.Message);
    }

    SwStatusResult Unavailable(SwStatusResult result, SwAgentError error)
    {
        result.Error = error;
        result.State = error.Code switch
        {
            ErrorCode.SwNotRunning => SupervisorState.NotRunning,
            ErrorCode.SwMultipleInstances => SupervisorState.Multiple,
            ErrorCode.SwStarting => SupervisorState.Starting,
            ErrorCode.SwCrashed => SupervisorState.Crashed,
            ErrorCode.Busy when IsLoading(error) => SupervisorState.Loading,
            _ => SupervisorState.AddinUnavailable,
        };
        if (error.Details != null && error.Details.TryGetValue("process_id", out var pid) && pid is long p) result.ProcessId = (int)p;
        else if (result.ProcessIds.Count == 1) result.ProcessId = result.ProcessIds[0];
        result.Message = result.State switch
        {
            SupervisorState.NotRunning => "SOLIDWORKS is not running; start it from the 3DEXPERIENCE dashboard in the browser, and the next call connects to it",
            SupervisorState.Multiple => $"{result.ProcessIds.Count} SOLIDWORKS instances are running; pick one with sw_reconnect {{process_id}}",
            SupervisorState.Starting => error.Message,
            _ => error.Message,
        };
        return result;
    }

    /// <summary>A busy that means the add-in is still loading (or another call is connecting while it loads).</summary>
    static bool IsLoading(SwAgentError error) => error.Code == ErrorCode.Busy && error.Details?["loading"] is true;

    static string ReadyMessage(int pid, AddinStateResult addin, SwStatusResult status)
    {
        if (status.Responding == false)
            return $"SOLIDWORKS (process {pid}) is not responding (for {status.NotRespondingMs} ms); swmcp never ends SOLIDWORKS, so wait, or deal with it yourself";
        if (addin.Dialog != null)
        {
            return $"SOLIDWORKS is showing the dialog \"{addin.Dialog.Title}\" (buttons: {string.Join(", ", addin.Dialog.Buttons.Select(b => b.Text))}); " +
                   $"nothing else runs until it is answered with sw_dialog_respond (dialog {addin.Dialog.Id})";
        }
        if (addin.Running != null)
            return $"SOLIDWORKS is running {addin.Running.Method} (for {addin.Running.RunningMs} ms), with {addin.Queued.Count} more waiting";
        return $"SOLIDWORKS (process {pid}) is ready";
    }

    /// <summary>One connection to one SOLIDWORKS, and what ended it.</summary>
    sealed class Link
    {
        readonly Supervisor _owner;
        readonly TaskCompletionSource<SwAgentError> _classified = new TaskCompletionSource<SwAgentError>(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool _dropped;
        SwAgentClient? _client;

        public Link(Supervisor owner, int processId)
        {
            _owner = owner;
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public SwAgentClient Client => _client!;

        /// <summary>What ended this connection; completes when it ends.</summary>
        public Task<SwAgentError> Classified => _classified.Task;

        public void Start(SwAgentClient client)
        {
            _client = client;
            _ = MonitorAsync(client);
        }

        /// <summary>After reconnecting to the same process: the events missed while disconnected.</summary>
        public async Task CatchUpAsync(CancellationToken cancellationToken)
        {
            if (_owner.Events.ProcessId != ProcessId) return;
            try
            {
                var state = await Client.CallAsync<AddinStateRequest, AddinStateResult>(new AddinStateRequest(), OperationFamilies.DefaultDeadline("addin"), cancellationToken)
                    .ConfigureAwait(false);
                if (_owner.Events.LastAddinSeq < state.LastEventSeq)
                {
                    var missed = await Client.CallAsync<AddinEventsRequest, AddinEventsResult>(
                            new AddinEventsRequest { AfterSeq = _owner.Events.LastAddinSeq }, OperationFamilies.DefaultDeadline("addin"), cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var e in missed.Events) _owner.Events.Add(ProcessId, e);
                }
            }
            catch (SwAgentException e)
            {
                _owner._log.LogWarning("could not catch up on events after connecting: {Message}", e.Message);
            }
        }

        public void OnNotification(string method, JsonObject parameters)
        {
            if (method != EventEnvelope.Method) return;
            EventEnvelope envelope;
            try
            {
                envelope = JsonCodec.FromJson<EventEnvelope>(parameters);
            }
            catch (SwAgentException e)
            {
                _owner._log.LogWarning("ignored an event that does not match the contract: {Message}", e.Message);
                return;
            }
            _owner.Events.Add(ProcessId, envelope);
            try
            {
                _owner.EventReceived?.Invoke(envelope);
            }
            catch (Exception e)
            {
                _owner._log.LogError(e, "an event handler failed");
            }
        }

        /// <summary>Ends the connection on purpose (reconnect, shutdown).</summary>
        public void Drop()
        {
            _dropped = true;
            _client?.Dispose();
        }

        async Task MonitorAsync(SwAgentClient client)
        {
            var reason = await client.Closed.ConfigureAwait(false);
            if (!_dropped) _owner.Ended(this, reason);
            _classified.TrySetResult(reason);
        }
    }
}
