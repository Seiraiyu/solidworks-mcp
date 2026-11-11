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
        ShowBox = box => DiagBoxes.Show(Runtime.MainWindow, box);
    }

    /// <summary>Shows diag.arm_dialog's message box and waits for its answer (ok, cancel, yes, no); replaced by tests.</summary>
    public Func<SwAgent.Contracts.DiagDialogRequest, string> ShowBox { get; set; }

    public ISolidWorksApplication SolidWorks { get; }

    public string AddinVersion { get; }

    public AgentRuntime Runtime { get; }

    /// <summary>Issues and resolves references, following documents saved under a new name.</summary>
    public ReferenceService References { get; }

    /// <summary>Applied on session.hello; null when preferences are not managed (tests).</summary>
    public PreferenceGuard? Preferences { get; set; }

    /// <summary>Sets export.part's preferences for the call and puts them back, journalled; null when not set up (tests that do not export), and export.part then answers api_error.</summary>
    public ScopedPreferences? ExportPreferences { get; set; }

    /// <summary>How api.call reaches SOLIDWORKS' members: SolidWorksBinder in the add-in, the plain late binder in tests.</summary>
    public LateBinder ApiBinder { get; set; } = new LateBinder();

    /// <summary>Where api.call logs every call (api-calls.log); null when not logged (tests that do not look).</summary>
    public ApiCallLog? ApiCalls { get; set; }

    /// <summary>Requests waiting on the UI thread.</summary>
    public Func<int> QueuedRequests { get; set; }
}
