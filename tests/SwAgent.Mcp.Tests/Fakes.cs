using System;
using System.Collections.Generic;
using SwAgent.Client;

namespace SwAgent.Mcp.Tests;

sealed class FakeProcesses : IProcessList
{
    readonly int[] _ids;

    public FakeProcesses(params int[] ids)
    {
        _ids = ids;
    }

    public IReadOnlyList<int> SolidWorksProcessIds() => _ids;
}

sealed class FakePipes : IPipeDirectory
{
    readonly Func<string, bool> _exists;

    public FakePipes(Func<string, bool> exists)
    {
        _exists = exists;
    }

    public bool Exists(string pipeName) => _exists(pipeName);
}

sealed class FakeAttach : ISolidWorksAttach
{
    readonly Func<int, string, int> _load;

    public FakeAttach(Func<int, string, int> load)
    {
        _load = load;
    }

    /// <summary>What Probe answers; ready unless a test says otherwise.</summary>
    public SolidWorksReadiness Readiness { get; set; } = SolidWorksReadiness.Yes;

    public bool Hung { get; set; }

    public int Loads { get; private set; }

    public SolidWorksReadiness Probe(int processId) => Readiness;

    public bool IsHung(int processId) => Hung;

    public int LoadAddIn(int processId, string dllPath)
    {
        Loads++;
        return _load(processId, dllPath);
    }
}

sealed class FakeRegistration : IAddinRegistration
{
    public AddinRegistrationInfo Info { get; set; } = new AddinRegistrationInfo(true, @"C:\addin\SwAgent.AddIn.dll", true);

    public AddinRegistrationInfo Read() => Info;
}

/// <summary>SOLIDWORKS processes that exit when a test says so.</summary>
sealed class FakeProcessWatch : IProcessWatch
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.Tasks.TaskCompletionSource<int?>> _exits =
        new System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.Tasks.TaskCompletionSource<int?>>();

    /// <summary>The token of every watch started, in order.</summary>
    public System.Collections.Generic.List<System.Threading.CancellationToken> Watches { get; } = new System.Collections.Generic.List<System.Threading.CancellationToken>();

    public System.Threading.Tasks.Task<int?> WatchAsync(int processId, System.Threading.CancellationToken cancellationToken)
    {
        lock (Watches) Watches.Add(cancellationToken);
        return Exit(processId).Task;
    }

    /// <summary>Process <paramref name="processId"/> exits with <paramref name="code"/>.</summary>
    public void Exited(int processId, int? code) => Exit(processId).TrySetResult(code);

    /// <summary>A new process with the same id (the test process stands in for every SOLIDWORKS).</summary>
    public void Restarted(int processId) => _exits[processId] = new System.Threading.Tasks.TaskCompletionSource<int?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    System.Threading.Tasks.TaskCompletionSource<int?> Exit(int processId) =>
        _exits.GetOrAdd(processId, _ => new System.Threading.Tasks.TaskCompletionSource<int?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously));
}
