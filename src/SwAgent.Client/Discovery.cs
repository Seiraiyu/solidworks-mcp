using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Client;

/// <summary>The sldworks.exe processes in the current Windows session.</summary>
public interface IProcessList
{
    IReadOnlyList<int> SolidWorksProcessIds();
}

public sealed class SystemProcessList : IProcessList
{
    public static readonly SystemProcessList Instance = new SystemProcessList();

    public IReadOnlyList<int> SolidWorksProcessIds()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var ids = new List<int>();
        foreach (var p in Process.GetProcessesByName("SLDWORKS"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session) ids.Add(p.Id);
                }
                catch (InvalidOperationException)
                {
                    // Exited while we looked.
                }
            }
        }
        ids.Sort();
        return ids;
    }
}

/// <summary>Whether a pipe exists, without connecting to it (connecting would take the add-in's one client slot).</summary>
public interface IPipeDirectory
{
    bool Exists(string pipeName);
}

public sealed class SystemPipeDirectory : IPipeDirectory
{
    public static readonly SystemPipeDirectory Instance = new SystemPipeDirectory();

    public bool Exists(string pipeName) =>
        Directory.EnumerateFiles(@"\\.\pipe\")
            .Any(path => string.Equals(Path.GetFileName(path), pipeName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Finds the SOLIDWORKS instance to talk to (phase 1: exactly one, or the one named), and checks its add-in is serving.</summary>
public sealed class AddinLocator
{
    readonly IProcessList _processes;
    readonly IPipeDirectory _pipes;
    readonly Func<int, string> _pipeNameFor;

    public AddinLocator(IProcessList processes, IPipeDirectory pipes, Func<int, string>? pipeNameFor = null)
    {
        _processes = processes;
        _pipes = pipes;
        _pipeNameFor = pipeNameFor ?? Protocol.PipeName;
    }

    /// <summary>The process id of the SOLIDWORKS to connect to; throws a typed <see cref="SwAgentException"/> otherwise.</summary>
    public int Locate(int? processId)
    {
        int pid = LocateProcess(processId);
        if (!_pipes.Exists(_pipeNameFor(pid)))
        {
            throw new SwAgentException(ErrorCode.AddinNotLoaded,
                $"SOLIDWORKS (process {pid}) is running but the add-in is not loaded; run `swmcp addin load` (after `swmcp addin register` if it is not registered)",
                new JsonObject { ["process_id"] = (long)pid });
        }
        return pid;
    }

    /// <summary>The SOLIDWORKS process meant (the one named, or the only one running), whether or not its add-in is up.</summary>
    public int LocateProcess(int? processId)
    {
        var ids = _processes.SolidWorksProcessIds();
        int pid;
        if (processId is int wanted)
        {
            if (!ids.Contains(wanted))
            {
                throw new SwAgentException(ErrorCode.SwNotRunning,
                    $"there is no SOLIDWORKS process {wanted} in this Windows session; running: {Describe(ids)}",
                    new JsonObject { ["process_id"] = (long)wanted, ["process_ids"] = Ids(ids) });
            }
            pid = wanted;
        }
        else if (ids.Count == 0)
        {
            throw new SwAgentException(ErrorCode.SwNotRunning,
                "SOLIDWORKS is not running; start it from the 3DEXPERIENCE launcher");
        }
        else if (ids.Count > 1)
        {
            throw new SwAgentException(ErrorCode.SwMultipleInstances,
                $"{ids.Count} SOLIDWORKS instances are running ({Describe(ids)}); name one with --pid",
                new JsonObject { ["process_ids"] = Ids(ids) });
        }
        else
        {
            pid = ids[0];
        }
        return pid;
    }

    static string Describe(IReadOnlyList<int> ids) => ids.Count == 0 ? "none" : string.Join(", ", ids);

    static List<object?> Ids(IReadOnlyList<int> ids) => ids.Select(i => (object?)(long)i).ToList();
}
