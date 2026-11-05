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
    Supervisor? _supervisor;

    /// <param name="name">The test class's run, for example fault-suite; part of the run id.</param>
    protected LiveRun(string name)
    {
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
                _supervisor?.Dispose();
            }
            finally
            {
                WriteReport();
            }
        }
    }

    void WriteReport()
    {
        var header = new ReportHeader
        {
            RunId = Id,
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
