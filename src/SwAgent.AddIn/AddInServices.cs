using System;

namespace SwAgent.AddIn;

/// <summary>What every operation is constructed with: the registry passes it to each operation's constructor.</summary>
public sealed class AddInServices
{
    public AddInServices(ISolidWorksApplication solidWorks, string addinVersion)
    {
        SolidWorks = solidWorks;
        AddinVersion = addinVersion;
    }

    public ISolidWorksApplication SolidWorks { get; }

    public string AddinVersion { get; }

    /// <summary>Requests waiting on the UI thread; wired to the dispatcher once the host has started.</summary>
    public Func<int> QueuedRequests { get; set; } = () => 0;
}
