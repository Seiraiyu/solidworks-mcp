using System;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn.Tests;

/// <summary>Which per-component read of what a view shows (IView.GetVisibleEntities2 with a component) a test means.</summary>
enum SweepKind
{
    /// <summary>VisibleEntities: edges, silhouette edges and vertices (view.entities, ShownEntities).</summary>
    Entities,

    /// <summary>VisibleFaces (ShownEntities).</summary>
    Faces,

    /// <summary>Shows (an entity named for an attachment, checked to be shown).</summary>
    Shows,
}

/// <summary>
/// The fake drawing's per-component sweeps run inside the seam's own guard (ViewSweepGuard), as SolidWorksDrawing runs
/// them, against a running command the fake keeps: a test makes one sweep open a PropertyManager, as SP4 did (5.D live
/// run, round 4), and sees whether the operation refuses and whether the command is cleared.
/// </summary>
sealed partial class FakeDrawing
{
    /// <summary>The command SOLIDWORKS has open (ISldWorks.GetRunningCommandInfo's title), or null.</summary>
    public string? RunningCommand { get; set; }

    /// <summary>When set, a per-component sweep of this kind opens <see cref="SweepOpens"/>.</summary>
    public SweepKind? SweepOpensOn { get; set; }

    /// <summary>The command that sweep leaves SOLIDWORKS in.</summary>
    public string SweepOpens { get; set; } = "Drawing View8";

    /// <summary>When true, clearing the selection does not close the command the sweep opened.</summary>
    public bool SweepCommandStaysOpen { get; set; }

    /// <summary>How many times a sweep's command was cleared (IModelDoc2.ClearSelection2).</summary>
    public int SweepClears { get; private set; }

    T Swept<T>(string view, string? component, SweepKind kind, Func<T> read)
    {
        if (component == null) return read();
        return ViewSweepGuard.Read(() => RunningCommand, () =>
        {
            SweepClears++;
            if (!SweepCommandStaysOpen) RunningCommand = null;
        }, view, $"fake {kind} sweep of {component}", () =>
        {
            var result = read();
            if (SweepOpensOn == kind) RunningCommand = SweepOpens;
            return result;
        });
    }
}
