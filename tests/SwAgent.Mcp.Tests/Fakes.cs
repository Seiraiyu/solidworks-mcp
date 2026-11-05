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
