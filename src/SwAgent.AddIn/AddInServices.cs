using System;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>What every UI-thread operation is constructed with: the registry passes it to each operation's constructor.</summary>
public sealed class AddInServices
{
    public AddInServices(ISolidWorksApplication solidWorks, string addinVersion, AgentRuntime? runtime = null)
    {
        SolidWorks = solidWorks;
        AddinVersion = addinVersion;
        Runtime = runtime ?? new AgentRuntime(addinVersion, 0, NullLog.Instance);
        QueuedRequests = () => Runtime.Ledger.QueuedCount;
        References = new ReferenceService(Runtime.Renames);
    }

    public ISolidWorksApplication SolidWorks { get; }

    public string AddinVersion { get; }

    public AgentRuntime Runtime { get; }

    /// <summary>Issues and resolves references, following documents saved under a new name.</summary>
    public ReferenceService References { get; }

    /// <summary>Applied on session.hello; null when preferences are not managed (tests).</summary>
    public PreferenceGuard? Preferences { get; set; }

    /// <summary>Requests waiting on the UI thread.</summary>
    public Func<int> QueuedRequests { get; set; }
}
