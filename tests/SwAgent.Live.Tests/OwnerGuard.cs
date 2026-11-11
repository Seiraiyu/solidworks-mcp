using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;

namespace SwAgent.Live.Tests;

/// <summary>How the guard ends a SOLIDWORKS: always by process id, never by name.</summary>
public interface ISolidWorksProcesses
{
    /// <summary>The process's name (SLDWORKS for SOLIDWORKS), or null when no process has that id.</summary>
    string? NameOf(int processId);

    /// <summary>Ends the process at once, as a crash would.</summary>
    void Kill(int processId);

    /// <summary>Asks its main window to close, as the user's File > Exit does; true when it exited within <paramref name="wait"/>.</summary>
    bool Close(int processId, TimeSpan wait);
}

public sealed class SystemSolidWorksProcesses : ISolidWorksProcesses
{
    public static readonly SystemSolidWorksProcesses Instance = new SystemSolidWorksProcesses();

    public string? NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Kill(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill();
        process.WaitForExit(30_000);
    }

    public bool Close(int processId, TimeSpan wait)
    {
        using var process = Process.GetProcessById(processId);
        process.CloseMainWindow();
        return process.WaitForExit((int)wait.TotalMilliseconds);
    }
}

/// <summary>The guard refused to end SOLIDWORKS; the case is skipped and the run goes on.</summary>
public sealed class GuardRefusedException : Exception
{
    public GuardRefusedException(string message, IReadOnlyList<DocumentInfo> documents)
        : base(message)
    {
        Documents = documents;
    }

    public IReadOnlyList<DocumentInfo> Documents { get; }
}

/// <summary>
/// Guards the owner's working SOLIDWORKS (design 363, Testing). The suite ends SOLIDWORKS only through here: it reads
/// the add-in's state again immediately before every kill or close, and refuses (GuardRefusedException) when any
/// document is open that this run did not create, when a dialog is open, or when the process is not the SOLIDWORKS the
/// add-in speaks for. It acts by process id only. It is the path for a client that stays connected across the end of
/// SOLIDWORKS, where hold-and-check.ps1 cannot connect: the add-in serves one client, and the suite holds it.
/// </summary>
public sealed class OwnerGuard
{
    readonly ISolidWorksProcesses _processes;
    readonly HashSet<string> _created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public OwnerGuard(string scratchDirectory, ISolidWorksProcesses processes)
    {
        ScratchDirectory = scratchDirectory;
        _processes = processes;
    }

    /// <summary>This run's own directory under the scratch root; every document the suite creates is saved here.</summary>
    public string ScratchDirectory { get; }

    /// <summary>The documents this run created, by full path.</summary>
    public IReadOnlyCollection<string> CreatedPaths => _created;

    /// <summary>A path for a new scratch document named <paramref name="name"/> (a part unless <paramref name="extension"/> says otherwise); creates the directory.</summary>
    public string NewScratchPath(string name, string extension = ".SLDPRT")
    {
        Directory.CreateDirectory(ScratchDirectory);
        return Path.Combine(ScratchDirectory, name + extension);
    }

    /// <summary>Records a document this run created, in memory and in created.txt beside it.</summary>
    public void Created(string path)
    {
        string full = Path.GetFullPath(path);
        _created.Add(full);
        Directory.CreateDirectory(ScratchDirectory);
        File.AppendAllText(Path.Combine(ScratchDirectory, "created.txt"), full + Environment.NewLine);
    }

    /// <summary>The open documents this run did not create; a document never saved has no path, so it is never ours.</summary>
    public IReadOnlyList<DocumentInfo> Foreign(AddinStateResult state) =>
        state.Documents.Where(d => d.Path == null || !_created.Contains(Path.GetFullPath(d.Path))).ToList();

    /// <summary>
    /// Ends SOLIDWORKS process <paramref name="processId"/> at once, after checking again. Only while the suite's
    /// diag.sleep holds the UI thread: then nothing (the user included) can open a document between the check and the
    /// kill. Returns the state it checked and the Stopwatch timestamps of the kill and of the process's exit.
    /// </summary>
    public async Task<KillResult> KillAsync(int processId, Func<Task<AddinStateResult>> readState)
    {
        var state = await CheckAsync(processId, readState).ConfigureAwait(false);
        if (state.Running?.Method != "diag.sleep")
        {
            throw new GuardRefusedException(
                $"SOLIDWORKS is not held by the suite's diag.sleep (running: {state.Running?.Method ?? "nothing"}), so a document could open before the kill; nothing was ended",
                Array.Empty<DocumentInfo>());
        }
        long killedAt = Stopwatch.GetTimestamp();
        _processes.Kill(processId);
        return new KillResult(state, killedAt, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Closes SOLIDWORKS process <paramref name="processId"/> normally, after checking again; true when it exited within
    /// <paramref name="wait"/>. One that does not exit is left alone, never killed. Nothing holds the UI thread here,
    /// so a window of milliseconds remains between the check and the close: a clean document opened in it would be
    /// closed with SOLIDWORKS, and a dirty one would stop the close at a save prompt (reported, not answered).
    /// </summary>
    public async Task<bool> CloseAsync(int processId, Func<Task<AddinStateResult>> readState, TimeSpan wait)
    {
        var state = await CheckAsync(processId, readState).ConfigureAwait(false);
        var dirty = state.Documents.Where(d => d.Dirty).ToList();
        if (dirty.Count > 0)
        {
            throw new GuardRefusedException(
                "a document has unsaved changes, so a normal close would stop at a save prompt: " +
                string.Join(", ", dirty.Select(d => d.Title)), dirty);
        }
        return _processes.Close(processId, wait);
    }

    /// <summary>
    /// Reads the add-in's state now and refuses (GuardRefusedException) unless the process is the SOLIDWORKS the add-in
    /// speaks for, only this run's documents are open, and no dialog is up. Kill and close call it; so does the canary
    /// just before it draws.
    /// </summary>
    public async Task<AddinStateResult> CheckAsync(int processId, Func<Task<AddinStateResult>> readState)
    {
        string? name = _processes.NameOf(processId);
        if (!string.Equals(name, "SLDWORKS", StringComparison.OrdinalIgnoreCase))
            throw new GuardRefusedException($"process {processId} is {(name == null ? "gone" : name)}, not SOLIDWORKS; nothing was ended", Array.Empty<DocumentInfo>());

        var state = await readState().ConfigureAwait(false);
        if (state.ProcessId != processId)
        {
            throw new GuardRefusedException(
                $"the add-in answered for process {state.ProcessId}, not {processId}; nothing was ended", Array.Empty<DocumentInfo>());
        }
        var foreign = Foreign(state);
        if (foreign.Count > 0)
        {
            throw new GuardRefusedException(
                "documents this run did not create are open, so SOLIDWORKS was not ended: " +
                string.Join(", ", foreign.Select(d => $"{d.Title} ({d.Path ?? "never saved"})")), foreign);
        }
        if (state.Dialog != null)
        {
            throw new GuardRefusedException(
                $"SOLIDWORKS is showing a dialog ('{state.Dialog.Title}'), so it was not ended", Array.Empty<DocumentInfo>());
        }
        return state;
    }
}

/// <summary>What a kill checked, and when it happened (Stopwatch timestamps): the kill itself, and the process gone.</summary>
public sealed record KillResult(AddinStateResult State, long KilledAt, long ExitedAt);
