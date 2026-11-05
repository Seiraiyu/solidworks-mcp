using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

/// <summary>Where the add-in is registered for this user.</summary>
public sealed class AddinRegistrationInfo
{
    public AddinRegistrationInfo(bool registered, string? path, bool fileExists)
    {
        Registered = registered;
        Path = path;
        FileExists = fileExists;
    }

    public bool Registered { get; }
    public string? Path { get; }
    public bool FileExists { get; }
}

/// <summary>Reads the add-in's per-user registration (swmcp's registrar in production).</summary>
public interface IAddinRegistration
{
    AddinRegistrationInfo Read();
}

/// <summary>What <see cref="AddinLoader.EnsureLoadedAsync"/> did.</summary>
public sealed class AddinLoadResult
{
    public AddinLoadResult(int processId, string? path, bool alreadyLoaded, int? loadAddInResult)
    {
        ProcessId = processId;
        Path = path;
        AlreadyLoaded = alreadyLoaded;
        LoadAddInResult = loadAddInResult;
    }

    public int ProcessId { get; }
    public string? Path { get; }
    public bool AlreadyLoaded { get; }
    public int? LoadAddInResult { get; }
}

/// <summary>
/// Gets the add-in serving in a SOLIDWORKS process: nothing when its pipe is up; otherwise, once SOLIDWORKS has
/// finished starting, ISldWorks.LoadAddIn with the registered path on an STA thread, then a wait for the pipe. One load
/// at a time per process: a call that stops waiting (its deadline ended) leaves the load running, and the next call
/// waits for that same load rather than starting another. A load that fails is remembered for that process and
/// reported again, not retried, until <see cref="Forget"/> (sw_reconnect), so a broken add-in is reported rather than
/// reloaded in a loop.
/// </summary>
public sealed class AddinLoader
{
    public static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "logs");

    /// <summary>RPC_E_CALL_REJECTED and RPC_E_SERVERCALL_RETRYLATER: SOLIDWORKS was too busy to take the call.</summary>
    static readonly string[] BusyHresults = { "0x80010001", "0x8001010A" };

    readonly ISolidWorksAttach _solidWorks;
    readonly IAddinRegistration _registration;
    readonly IPipeDirectory _pipes;
    readonly Func<int, string> _pipeNameFor;
    readonly ILogger _log;
    readonly object _gate = new object();
    readonly Dictionary<int, SwAgentError> _failed = new Dictionary<int, SwAgentError>();
    readonly Dictionary<int, Task<Loading>> _loading = new Dictionary<int, Task<Loading>>();

    public AddinLoader(ISolidWorksAttach solidWorks, IAddinRegistration registration, IPipeDirectory pipes,
        Func<int, string>? pipeNameFor = null, ILogger? log = null)
    {
        _solidWorks = solidWorks;
        _registration = registration;
        _pipes = pipes;
        _pipeNameFor = pipeNameFor ?? Protocol.PipeName;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>How long to wait for the pipe after LoadAddIn returns before the load counts as failed.</summary>
    public TimeSpan PipeWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the message filter retries a LoadAddIn that SOLIDWORKS rejects as busy.</summary>
    public TimeSpan ComBound { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest a readiness probe may take (never longer than the caller's deadline).</summary>
    public TimeSpan ProbeBound { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True while a LoadAddIn into <paramref name="processId"/> is running or its pipe is awaited.</summary>
    public bool IsLoading(int processId)
    {
        lock (_gate) return _loading.ContainsKey(processId);
    }

    /// <summary>
    /// Whether SOLIDWORKS process <paramref name="processId"/> has finished starting, asked on an STA thread within
    /// <paramref name="deadlineUtc"/>. A probe SOLIDWORKS does not answer in time means it is still starting.
    /// </summary>
    public async Task<SolidWorksReadiness> ProbeAsync(int processId, DateTime deadlineUtc)
    {
        try
        {
            return await StaCall.RunAsync("the readiness check", () => _solidWorks.Probe(processId), Bound(ProbeBound, deadlineUtc)).ConfigureAwait(false);
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.Busy)
        {
            return new SolidWorksReadiness(false, "it does not answer yet");
        }
    }

    /// <summary>
    /// Makes sure the add-in serves process <paramref name="processId"/> before <paramref name="deadlineUtc"/>. Throws
    /// sw_starting while SOLIDWORKS is starting; addin_not_loaded when the add-in is not registered or its pipe never
    /// appears; api_error when LoadAddIn fails; busy (started false, loading true) when the deadline ends while the add-in
    /// is still loading, or when SOLIDWORKS was too busy to take LoadAddIn.
    /// </summary>
    public async Task<AddinLoadResult> EnsureLoadedAsync(int processId, DateTime deadlineUtc, CancellationToken cancellationToken = default)
    {
        string pipe = _pipeNameFor(processId);
        if (_pipes.Exists(pipe)) return new AddinLoadResult(processId, null, alreadyLoaded: true, loadAddInResult: null);

        Task<Loading>? load;
        lock (_gate)
        {
            if (_failed.TryGetValue(processId, out var failed)) throw new SwAgentException(failed);
            _loading.TryGetValue(processId, out load);
        }
        if (load == null)
        {
            string path = RegisteredPath();
            var readiness = await ProbeAsync(processId, deadlineUtc).ConfigureAwait(false);
            if (!readiness.Ready)
            {
                throw new SwAgentException(ErrorCode.SwStarting,
                    $"SOLIDWORKS (process {processId}) has not finished starting: {readiness.Reason}; wait for it, and answer any sign-in or update prompt in SOLIDWORKS yourself",
                    new JsonObject { ["process_id"] = (long)processId, ["reason"] = readiness.Reason });
            }
            lock (_gate)
            {
                // Recorded before it is awaited, so no other call can start a second LoadAddIn meanwhile.
                if (!_loading.TryGetValue(processId, out load))
                {
                    load = LoadAsync(processId, path, pipe);
                    _loading[processId] = load;
                }
            }
        }

        var left = deadlineUtc - DateTime.UtcNow;
        try
        {
            var loading = await load.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            return new AddinLoadResult(processId, loading.Path, alreadyLoaded: false, loadAddInResult: loading.Result);
        }
        catch (TimeoutException)
        {
            throw new SwAgentException(ErrorCode.Busy,
                $"the add-in is still loading into SOLIDWORKS (process {processId}); nothing was run, so call again",
                new JsonObject { ["process_id"] = (long)processId, ["started"] = false, ["loading"] = true });
        }
    }

    /// <summary>The registered add-in DLL; throws addin_not_loaded, saying to run swmcp addin register, when there is none.</summary>
    public string RegisteredPath()
    {
        var registration = _registration.Read();
        if (registration.Registered && registration.Path != null) return registration.Path;
        throw new SwAgentException(ErrorCode.AddinNotLoaded,
            registration.Path != null && !registration.FileExists
                ? $"the add-in is registered at {registration.Path}, which no longer exists; run `swmcp addin register` first"
                : "the add-in is not registered for this user; run `swmcp addin register` first",
            new JsonObject { ["registered_path"] = registration.Path, ["file_exists"] = registration.FileExists });
    }

    /// <summary>Forgets a failed load, so the next call loads again. A load still running is left to finish.</summary>
    public void Forget(int processId)
    {
        lock (_gate) _failed.Remove(processId);
    }

    /// <summary>One LoadAddIn and the wait for its pipe; its outcome is the load's, whoever is still waiting for it.</summary>
    async Task<Loading> LoadAsync(int processId, string path, string pipe)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            int result;
            try
            {
                result = await StaCall.Start("LoadAddIn", () => _solidWorks.LoadAddIn(processId, path), ComBound).ConfigureAwait(false);
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ApiError && e.Error.Details?["hresult"] is string h && Array.IndexOf(BusyHresults, h) >= 0)
            {
                _log.LogWarning("LoadAddIn into process {Pid} was refused while SOLIDWORKS was busy ({Hresult}); the next call tries again", processId, h);
                throw new SwAgentException(ErrorCode.Busy,
                    $"SOLIDWORKS (process {processId}) was too busy to load the add-in ({h}); nothing was run, so call again once it is idle",
                    new JsonObject { ["process_id"] = (long)processId, ["started"] = false, ["hresult"] = h });
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ApiError)
            {
                _log.LogError("LoadAddIn into process {Pid} failed: {Message}", processId, e.Message);
                throw Remember(processId, WithLogs(e.Error));
            }
            _log.LogInformation("LoadAddIn into process {Pid} returned {Result} after {Ms} ms; waiting for its pipe", processId, result, clock.ElapsedMilliseconds);

            var returned = Stopwatch.StartNew();
            while (!_pipes.Exists(pipe))
            {
                if (returned.Elapsed >= PipeWait)
                {
                    _log.LogError("the add-in's pipe for process {Pid} did not appear after LoadAddIn returned {Result}", processId, result);
                    throw Remember(processId, new SwAgentError(ErrorCode.AddinNotLoaded,
                        $"SOLIDWORKS (process {processId}) did not start the add-in: LoadAddIn returned {result} and \\\\.\\pipe\\{pipe} " +
                        $"did not appear within {PipeWait.TotalSeconds:0.#} s; the add-in's log is in {LogDirectory}. " +
                        "It is not loaded again until sw_reconnect",
                        new JsonObject
                        {
                            ["process_id"] = (long)processId,
                            ["path"] = path,
                            ["load_addin_result"] = (long)result,
                            ["log_directory"] = LogDirectory,
                        }));
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            _log.LogInformation("the add-in serves process {Pid}, {Ms} ms after LoadAddIn", processId, clock.ElapsedMilliseconds);
            return new Loading(path, result);
        }
        finally
        {
            lock (_gate) _loading.Remove(processId);
        }
    }

    SwAgentException Remember(int processId, SwAgentError error)
    {
        lock (_gate) _failed[processId] = error;
        return new SwAgentException(error);
    }

    static SwAgentError WithLogs(SwAgentError error)
    {
        var details = error.Details ?? new JsonObject();
        details["log_directory"] = LogDirectory;
        return new SwAgentError(error.Code, $"{error.Message}; the add-in's log is in {LogDirectory}. It is not loaded again until sw_reconnect", details);
    }

    /// <summary><paramref name="limit"/>, or less when the deadline is nearer; never under 100 ms.</summary>
    static TimeSpan Bound(TimeSpan limit, DateTime deadlineUtc)
    {
        var left = deadlineUtc - DateTime.UtcNow;
        var bound = left < limit ? left : limit;
        return bound < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : bound;
    }

    sealed class Loading
    {
        public Loading(string path, int result)
        {
            Path = path;
            Result = result;
        }

        public string Path { get; }
        public int Result { get; }
    }
}
