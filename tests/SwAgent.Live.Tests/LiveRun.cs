using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Mcp.Registration;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// One live run: its directory (progress.log, client.log, report.md), the supervisor the cases drive SOLIDWORKS
/// through (as swmcp serve does), the owner guard, and the Diagnostics switch, set at the start and cleared at the end.
/// Does nothing unless SWMCP_LIVE=1. The person running it follows progress.log: lines starting ACTION ask for
/// something only they can do (start SOLIDWORKS from the 3DEXPERIENCE tab, open a dialog), STOP ends the run.
/// Each test class has its own subclass, so its run has its own id, directory, scratch directory and report.
/// </summary>
public abstract class LiveRun : IDisposable
{
    readonly object _gate = new object();
    readonly List<CaseResult> _results = new List<CaseResult>();
    readonly DateTime _started = DateTime.Now;
    readonly ILogger _log = new FileLogger(null);
    readonly AddinStartup _startup = new AddinStartup(Registry.CurrentUser);
    readonly string _suite;
    Supervisor? _supervisor;

    /// <param name="name">The test class's run, for example fault-suite; part of the run id.</param>
    protected LiveRun(string name)
    {
        _suite = name.Replace('-', ' ');
        Id = $"{_started:yyyyMMdd-HHmmss-fff}-{name}";
        Directory = Path.Combine(RunsRoot, Id);
        Guard = new OwnerGuard(Path.Combine(LiveScratch.Root, Id), SystemSolidWorksProcesses.Instance);
        if (!LiveSettings.Live) return;
        System.IO.Directory.CreateDirectory(Directory);
        _log = new FileLogger(Path.Combine(Directory, "client.log"));
        SetDiagnostics(true);
        Say($"live run {Id}: progress in {ProgressPath}, report at the end in {Path.Combine(Directory, "report.md")}");
    }

    public static string RunsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "live-runs");

    /// <summary>The add-in's preference journal (PreferenceGuard.DefaultJournalPath): present while preferences are applied.</summary>
    public static string JournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "preferences-journal.json");

    public string Id { get; }

    public string Directory { get; }

    public string ProgressPath => Path.Combine(Directory, "progress.log");

    public OwnerGuard Guard { get; }

    /// <summary>Why the run stopped, once it has; every later case is skipped.</summary>
    public string? Stopped { get; private set; }

    public string? SwRevision { get; set; }

    public string? AddinVersion { get; set; }

    /// <summary>The supervisor the cases use, as one swmcp serve would; created on first use.</summary>
    public Supervisor Supervisor => _supervisor ??= NewSupervisor();

    public Supervisor NewSupervisor() => new Supervisor(new SupervisorOptions
    {
        Client = new ClientOptions { Logger = _log },
        Registration = new RegistrarAddinRegistration(new AddinRegistrar(Registry.CurrentUser)),
    });

    /// <summary>Drops the supervisor's connection (the add-in sees its client go) and starts a new, unconnected one.</summary>
    public void ReplaceSupervisor()
    {
        _supervisor?.Dispose();
        _supervisor = NewSupervisor();
    }

    /// <summary>The add-in's own state, through the supervisor's connection (a pipe-thread operation: answers while SOLIDWORKS is busy).</summary>
    public Task<AddinStateResult> AddinStateAsync() =>
        Supervisor.CallAsync<AddinStateRequest, AddinStateResult>(new AddinStateRequest(), TimeSpan.FromSeconds(10));

    public void Say(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_gate)
        {
            if (System.IO.Directory.Exists(Directory)) File.AppendAllText(ProgressPath, stamped + Environment.NewLine);
        }
        Console.WriteLine(stamped);
    }

    /// <summary>Ends the run: this case fails, every later one is skipped, and the report says why.</summary>
    public Exception Stop(string reason)
    {
        Stopped ??= reason;
        Say("STOP: " + reason);
        return new InvalidOperationException("run stopped: " + reason);
    }

    /// <summary>Runs one case and records its outcome and evidence for the report.</summary>
    public async Task CaseAsync(int step, string name, Func<Task<string>> body)
    {
        LiveSettings.RequireLive();
        if (Stopped != null)
        {
            Record(step, name, CaseOutcome.Skip, "run stopped: " + Stopped, 0);
            throw new SkipException("run stopped: " + Stopped);
        }
        Say($"case {step}: {name}");
        var clock = Stopwatch.StartNew();
        try
        {
            string evidence = await body().ConfigureAwait(false);
            Record(step, name, CaseOutcome.Pass, evidence, clock.Elapsed.TotalSeconds);
            Say($"PASS {name}: {evidence}");
        }
        catch (SkipException e)
        {
            Record(step, name, CaseOutcome.Skip, e.Message, clock.Elapsed.TotalSeconds);
            Say($"SKIP {name}: {e.Message}");
            throw;
        }
        catch (GuardRefusedException e)
        {
            Record(step, name, CaseOutcome.Skip, "owner guard: " + e.Message, clock.Elapsed.TotalSeconds);
            Say($"SKIP {name}: owner guard: {e.Message}");
            throw new SkipException("owner guard: " + e.Message);
        }
        catch (Exception e)
        {
            Record(step, name, CaseOutcome.Fail, $"{e.GetType().Name}: {e.Message}", clock.Elapsed.TotalSeconds);
            Say($"FAIL {name}: {e.Message}");
            throw;
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 100 ms until true; throws TimeoutException naming <paramref name="what"/>.</summary>
    public static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (clock.Elapsed > timeout) throw new TimeoutException($"{what} did not happen within {timeout.TotalSeconds:0} s");
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the person running the suite to start SOLIDWORKS from the 3DEXPERIENCE tab, then asks sw_status every 2 s
    /// until it says ready: the supervisor finds the new SOLIDWORKS, waits while it starts and loads the add-in itself.
    /// Returns the ready status and every state seen.
    /// </summary>
    public async Task<(SwStatusResult Status, IReadOnlyList<string> Seen)> WaitForSolidWorksAsync(TimeSpan timeout)
    {
        Say($"ACTION: start SOLIDWORKS from the 3DEXPERIENCE tab now (waiting up to {timeout.TotalMinutes:0} min). On a sign-in or update prompt, stop the run and report.");
        var seen = new List<string>();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var status = await Supervisor.StatusAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            string label = status.State == SupervisorState.Ready && status.Addin?.Dialog != null
                ? $"ready (dialog '{status.Addin.Dialog.Title}')"
                : WireNames.ForEnum(status.State);
            if (seen.Count == 0 || seen[seen.Count - 1] != label)
            {
                seen.Add(label);
                Say($"sw_status: {label}: {status.Message}");
            }
            if (status.State == SupervisorState.Ready && status.Addin?.Dialog == null) return (status, seen);
            if (clock.Elapsed > timeout)
                throw Stop($"SOLIDWORKS was not ready within {timeout.TotalMinutes:0} min (states seen: {string.Join(" -> ", seen)})");
            await Task.Delay(2000).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes SOLIDWORKS ready for the run and returns its process id. The run owns the add-in's startup switch and
    /// Diagnostics (Diagnostics is already on: the constructor set it), so a run never depends on how SOLIDWORKS was
    /// last started. One SOLIDWORKS whose add-in is not loaded yet, or is loaded with diagnostics on, is ready. One whose
    /// add-in was loaded without diagnostics must be closed first: the run asks for that (ACTION; ending SOLIDWORKS is
    /// the owner's) and waits. With no SOLIDWORKS running, the run turns the add-in's startup switch off (put back when
    /// the run ends), asks for SOLIDWORKS to be started from the 3DEXPERIENCE tab, and waits for its window without
    /// loading the add-in: the run's first call loads it, with diagnostics on.
    /// </summary>
    public async Task<int> PrepareSolidWorksAsync()
    {
        while (true)
        {
            var ids = SystemProcessList.Instance.SolidWorksProcessIds();
            bool loaded = ids.Count == 1 && SystemPipeDirectory.Instance.Exists(Protocol.PipeName(ids[0]));
            var state = loaded ? await AddinStateAsync().ConfigureAwait(false) : null;
            switch (ReadinessCheck.Decide(ids.Count, loaded, state?.DiagnosticsEnabled == true))
            {
                case Readiness.Ready:
                    return ids[0];
                case Readiness.TooMany:
                    throw Stop($"{ids.Count} SOLIDWORKS processes are running; a run needs exactly one");
                case Readiness.CloseFirst:
                    if (state!.Documents.Count > 0)
                    {
                        throw Stop($"the add-in in SOLIDWORKS {ids[0]} was loaded without diagnostics, and documents are open " +
                            $"({string.Join(", ", state.Documents.Select(d => d.Path ?? d.Title))}); nothing was asked or changed");
                    }
                    ReplaceSupervisor();
                    Say($"ACTION: close SOLIDWORKS {ids[0]} now (File > Exit; no documents are open): its add-in was loaded without diagnostics. " +
                        "The run waits up to 15 min for it to exit, then asks for it to be started again.");
                    await WaitForExitAsync(ids[0], TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                    continue;
                default:
                    _startup.TurnOff();
                    Say(@"the add-in's startup switch (HKCU\Software\SolidWorks\AddInsStartup) is off for this run and is put back at the end, " +
                        "so SOLIDWORKS starts without the add-in and the run's first call loads it with diagnostics on");
                    return await WaitForSolidWorksWindowAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            }
        }
    }

    async Task WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (SystemProcessList.Instance.SolidWorksProcessIds().Contains(pid))
        {
            if (clock.Elapsed > timeout) throw Stop($"SOLIDWORKS {pid} did not exit within {timeout.TotalMinutes:0} min");
            await Task.Delay(2000).ConfigureAwait(false);
        }
        Say($"SOLIDWORKS {pid} exited");
    }

    /// <summary>Asks for SOLIDWORKS to be started from the 3DEXPERIENCE tab and waits for its main window, without calling the add-in.</summary>
    async Task<int> WaitForSolidWorksWindowAsync(TimeSpan timeout)
    {
        Say($"ACTION: start SOLIDWORKS from the 3DEXPERIENCE tab now (waiting up to {timeout.TotalMinutes:0} min). On a sign-in or update prompt, stop the run and report.");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var ids = SystemProcessList.Instance.SolidWorksProcessIds();
            if (ids.Count > 1) throw Stop($"{ids.Count} SOLIDWORKS processes are running; a run needs exactly one");
            if (ids.Count == 1)
            {
                using var process = Process.GetProcessById(ids[0]);
                if (process.MainWindowTitle.StartsWith("SOLIDWORKS", StringComparison.Ordinal))
                {
                    Say($"SOLIDWORKS {ids[0]} is up: '{process.MainWindowTitle}'");
                    return ids[0];
                }
            }
            if (clock.Elapsed > timeout)
                throw Stop($"SOLIDWORKS showed no main window within {timeout.TotalMinutes:0} min (a 3DEXPERIENCE sign-in or update prompt is the owner's to answer)");
            await Task.Delay(2000).ConfigureAwait(false);
        }
    }

    /// <summary>The one SOLIDWORKS process the add-in now speaks for; stops the run if the documents open are not all this run's.</summary>
    public async Task<AddinStateResult> RequireOnlyOwnDocumentsAsync()
    {
        var state = await AddinStateAsync().ConfigureAwait(false);
        var foreign = Guard.Foreign(state);
        if (foreign.Count > 0)
            throw Stop("documents the suite did not create are open (the owner's work): " + string.Join(", ", foreign.Select(d => $"{d.Title} ({d.Path ?? "never saved"})")));
        return state;
    }

    static void SetDiagnostics(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\solidworks-mcp");
        if (on) key.SetValue("Diagnostics", 1, RegistryValueKind.DWord);
        else key.DeleteValue("Diagnostics", throwOnMissingValue: false);
    }

    void Record(int step, string name, CaseOutcome outcome, string evidence, double seconds)
    {
        lock (_gate)
        {
            _results.RemoveAll(r => r.Step == step);
            _results.Add(new CaseResult(step, name, outcome, evidence, seconds));
        }
    }

    public void Dispose()
    {
        if (!LiveSettings.Live) return;
        try
        {
            // First, whatever else fails: diagnostics must never stay on after a run.
            SetDiagnostics(false);
            Say(@"Diagnostics cleared (HKCU\Software\solidworks-mcp\Diagnostics deleted)");
        }
        finally
        {
            try
            {
                if (_startup.Changed)
                {
                    _startup.Restore();
                    Say($"the add-in's startup switch put back to {(_startup.Original?.ToString() ?? "absent")}");
                }
            }
            finally
            {
                try
                {
                    CloseOwnDocuments();
                    _supervisor?.Dispose();
                }
                finally
                {
                    WriteReport();
                }
            }
        }
    }

    /// <summary>
    /// Best effort, when the run ends however it ends (a STOP skips the suite's own closing case): closes, discarding
    /// changes, every open document this run created, and nothing else. Never ends SOLIDWORKS; a failure is only said.
    /// </summary>
    void CloseOwnDocuments()
    {
        if (_supervisor == null || Guard.CreatedPaths.Count == 0) return;
        try
        {
            var closing = Task.Run(async () =>
            {
                var state = await AddinStateAsync().ConfigureAwait(false);
                foreach (var d in state.Documents.Where(d => d.Path != null && Guard.CreatedPaths.Contains(Path.GetFullPath(d.Path))))
                {
                    try
                    {
                        await Supervisor.CallAsync<DocumentCloseRequest, DocumentCloseResult>(
                            new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        Say($"closed this run's {d.Path}");
                    }
                    catch (Exception e)
                    {
                        Say($"could not close this run's {d.Path}: {e.Message}");
                    }
                }
            });
            if (!closing.Wait(TimeSpan.FromMinutes(2))) Say("closing this run's documents took over 2 min; left open");
        }
        catch (Exception e)
        {
            Say($"this run's documents were not closed: {(e is AggregateException a ? a.InnerException?.Message : e.Message)}");
        }
    }

    void WriteReport()
    {
        var header = new ReportHeader
        {
            RunId = Id,
            Suite = _suite,
            Machine = Environment.MachineName,
            SwRevision = SwRevision,
            AddinVersion = AddinVersion,
            ClientVersion = SwAgentClient.Version,
            Commit = CommitOf(SwAgentClient.Version),
            Started = _started,
            Finished = DateTime.Now,
            RunDirectory = Directory,
            Stopped = Stopped,
        };
        string report;
        lock (_gate) report = LiveReport.Render(header, _results.ToList());
        File.WriteAllText(Path.Combine(Directory, "report.md"), report);
        Say($"report written: {Path.Combine(Directory, "report.md")}");
    }

    /// <summary>The commit in a version such as 0.1.0+d820bb9...: what the build was made from; null when the version has none.</summary>
    public static string? CommitOf(string version)
    {
        int plus = version.IndexOf('+');
        return plus < 0 || plus == version.Length - 1 ? null : version.Substring(plus + 1);
    }
}

/// <summary>The client's log for a run: one line per entry, appended to a file (or dropped when there is none).</summary>
sealed class FileLogger : ILogger
{
    readonly string? _path;
    readonly object _gate = new object();

    public FileLogger(string? path)
    {
        _path = path;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _path != null && logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel} {formatter(state, exception)}{(exception != null ? " " + exception : "")}";
        lock (_gate) File.AppendAllText(_path!, line + Environment.NewLine);
    }
}
