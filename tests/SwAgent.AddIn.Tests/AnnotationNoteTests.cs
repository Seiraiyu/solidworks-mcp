using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AnnotationNoteTests : AnnotationTestBase
{
    AnnotationNoteResult Note(string text, SheetPoint at, ViewEntity? attachTo = null, string? view = null, string? sheet = null) =>
        Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest { Text = text, At = at, AttachTo = attachTo, View = view, Sheet = sheet });

    [Fact]
    public void A_sheet_note_shows_its_linked_title_resolved()
    {
        Drw.Props["Title"] = "BLOCK";
        var r = Note("TITLE: $PRP:\"Title\"", new SheetPoint(300, 30));
        Assert.Equal("TITLE: $PRP:\"Title\"", r.Text);
        Assert.Equal("TITLE: BLOCK", r.Shown);
        Assert.Null(r.Annotation.View);
        Assert.Equal("Sheet1", r.Annotation.Sheet);
        Assert.Equal(AnnotationKind.Note, r.Annotation.Kind);
        Assert.Empty(r.Annotation.Attached);
        Assert.Equal((0, 1), (r.Change.AnnotationsBefore, r.Change.AnnotationsAfter));
    }

    [Fact]
    public void A_note_with_a_leader_to_the_hole_goes_into_the_holes_view_attached_to_it()
    {
        var r = Note("THRU", new SheetPoint(130, 190), On(Hole));
        Assert.Equal(Front.Facts.Name, r.Annotation.View);
        Assert.Equal(On(Hole).Reference, Assert.Single(r.Annotation.Attached).Reference);
        Assert.Equal(130, r.Annotation.Position!.X, 9);
        Assert.Equal(1, Drw.Settled);
    }

    [Fact]
    public void A_note_in_a_view_without_a_leader()
    {
        var r = Note("SCALE 1:1", new SheetPoint(100, 120), view: Front.Facts.Name);
        Assert.Equal(Front.Facts.Name, r.Annotation.View);
        Assert.Empty(r.Annotation.Attached);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("owner")]
    [InlineData("attached")]
    public void A_note_SOLIDWORKS_put_elsewhere_or_left_unattached_is_refused_and_deleted(string check)
    {
        if (check == "position") Drw.AnnotationLandsOffByMm = 0.02;
        if (check == "owner") Drw.NotesLandOnSheet = true;
        if (check == "attached") Drw.AttachIgnored = true;
        var e = Fails(() => Note("THRU", new SheetPoint(130, 190), On(Hole)));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
        Assert.Equal(1, Drw.Settled);
    }

    [Fact]
    public void Requests_that_cannot_be_met_are_refused_before_SOLIDWORKS_is_asked()
    {
        Drw.AddView("Sheet1", Block, "top", new SheetPoint(100, 60));
        Assert.Equal("/text", Fails(() => Note(" ", new SheetPoint(300, 30))).Error.Details!["field"]);
        Assert.Equal("/attach_to", Fails(() => Note("X", new SheetPoint(300, 30), On(Back))).Error.Details!["field"]);
        Assert.Equal("/view", Fails(() => Note("X", new SheetPoint(300, 30), On(Hole), view: "Drawing View2")).Error.Details!["field"]);
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Assert.Equal("/sheet", Fails(() => Note("X", new SheetPoint(300, 30), view: Front.Facts.Name, sheet: "Sheet2")).Error.Details!["field"]);
        Assert.Equal("/at", Fails(() => Note("X", new SheetPoint(430, 30))).Error.Details!["field"]);
        Assert.DoesNotContain(Drw.Calls, c => c.StartsWith("note", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_note_on_another_sheet_goes_there()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Active = "Sheet1";
        var r = Note("SHEET 2", new SheetPoint(200, 100), sheet: "Sheet2");
        Assert.Equal("Sheet2", r.Annotation.Sheet);
        Assert.Single(Drw.Annotations("Sheet2", null));
    }
}
