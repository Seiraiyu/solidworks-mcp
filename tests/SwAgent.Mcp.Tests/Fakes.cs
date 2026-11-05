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
