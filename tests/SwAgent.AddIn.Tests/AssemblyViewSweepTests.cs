using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every per-component read of what an assembly view shows is guarded (ViewSweepGuard, PR #33 review): SP4 opened a
/// view's PropertyManager on one (5.D live run, round 4), so a read that leaves SOLIDWORKS in a command is cleared and
/// refused, whichever of the sweeps it was and whichever operation read it.
/// </summary>
public class AssemblyViewSweepTests : AssemblyDrawingTestBase
{
    ViewEntitiesResult Entities(string? component = null) =>
        Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = AsmFront.Facts.Name, Component = component });

    AnnotationNoteResult NoteOnPin() => Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest
    {
        Text = "PIN", At = new SheetPoint(300, 200),
        AttachTo = new ViewEntity { View = AsmFront.Facts.Name, Reference = Services.References.Describe(Asm, PinCircle).Reference },
    });

    void AssertRefusedAndCleared(SwAgentException e)
    {
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(AsmFront.Facts.Name, e.Error.Details!["view"]);
        Assert.Equal("Drawing View8", e.Error.Details["command"]);
        Assert.Equal(true, e.Error.Details["cleared"]);
        Assert.Contains("left SOLIDWORKS in 'Drawing View8'; it was cleared", e.Message);
        Assert.Null(Drw.RunningCommand);
        Assert.Equal(1, Drw.SweepClears);
    }

    [Fact]
    public void View_entities_sweep_of_a_component_that_leaves_a_command_open_is_refused_and_cleared()
    {
        Drw.SweepOpensOn = SweepKind.Entities;
        AssertRefusedAndCleared(Fails(() => Entities()));
    }

    [Fact]
    public void View_entities_of_one_component_that_leaves_a_command_open_is_refused_and_cleared()
    {
        Drw.SweepOpensOn = SweepKind.Entities;
        AssertRefusedAndCleared(Fails(() => Entities("pin-1")));
    }

    [Fact]
    public void An_attachment_whose_edge_sweep_leaves_a_command_open_is_refused_and_nothing_is_made()
    {
        Drw.SweepOpensOn = SweepKind.Entities;
        AssertRefusedAndCleared(Fails(() => NoteOnPin()));
        Assert.DoesNotContain(Drw.Annotated, a => a.Kind == AnnotationKind.Note);
    }

    [Fact]
    public void An_attachment_whose_face_sweep_leaves_a_command_open_is_refused_and_nothing_is_made()
    {
        Drw.SweepOpensOn = SweepKind.Faces;
        AssertRefusedAndCleared(Fails(() => NoteOnPin()));
        Assert.DoesNotContain(Drw.Annotated, a => a.Kind == AnnotationKind.Note);
    }

    [Fact]
    public void An_attachment_whose_shown_check_leaves_a_command_open_is_refused_and_nothing_is_made()
    {
        Drw.SweepOpensOn = SweepKind.Shows;
        AssertRefusedAndCleared(Fails(() => NoteOnPin()));
        Assert.DoesNotContain(Drw.Annotated, a => a.Kind == AnnotationKind.Note);
    }

    [Fact]
    public void Reading_back_a_proxy_attachment_whose_sweep_leaves_a_command_open_is_refused()
    {
        // ShownEntities.ShownOf sweeps every component for the proxy a note reads back attached to (case 32).
        Drw.AttachedAsProxies = true;
        Drw.SweepOpensOn = SweepKind.Faces;
        var e = Fails(() => NoteOnPin());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Null(Drw.RunningCommand);
    }

    [Fact]
    public void A_command_the_sweep_cannot_clear_is_named_as_still_open()
    {
        Drw.SweepOpensOn = SweepKind.Entities;
        Drw.SweepCommandStaysOpen = true;
        var e = Fails(() => Entities());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(false, e.Error.Details!["cleared"]);
        Assert.Contains("'Drawing View8' is still open: close it in SOLIDWORKS", e.Message);
    }

    [Fact]
    public void Sweeps_that_leave_nothing_open_answer_as_before()
    {
        Assert.Equal(new[] { "block-1", "pin-1" }, Entities().Entities.Select(e => e.Component));
        Assert.NotNull(Assert.Single(NoteOnPin().Annotation.Attached).Reference);
        Assert.Equal(0, Drw.SweepClears);
    }
}
