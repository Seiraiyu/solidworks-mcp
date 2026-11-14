using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>A title note on Sheet1, a reference dimension of the hole and the boss's imported depth in the front view; a note on Sheet2.</summary>
public class AnnotationEditTests : AnnotationTestBase
{
    readonly string _note;
    readonly string _diameter;
    readonly string _depth;

    public AnnotationEditTests()
    {
        var boss = Block.AddFeature("Boss-Extrude1", "Extrusion");
        Block.AddFeatureDimension(boss, "D1@Boss-Extrude1", 20, angle: false);
        Drw.ImportPlan.Add((Front.Facts.Name, AnnotationKind.Dimension, "D1@Boss-Extrude1"));
        Drw.ImportAnnotations(new[] { AnnotationImportType.Dimensions });
        _depth = Drw.Annotated.Single().Name;
        _note = Drw.AddNote("Sheet1", null, "TITLE", new SheetPoint(300, 30), null)!;
        _diameter = Drw.AddDimension(Front.Facts.Name, DimensionKind.Diameter, new object[] { Hole }, new SheetPoint(120, 190))!;
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.AddNote("Sheet2", null, "SHEET 2", new SheetPoint(100, 100), null);
        Drw.Active = "Sheet1";
        Drw.Calls.Clear();
    }

    AnnotationListResult List(string? sheet = null, string? view = null) =>
        Run(new AnnotationListOperation(Services), new AnnotationListRequest { Sheet = sheet, View = view });

    AnnotationResult Set(string annotation, SheetPoint? at = null, string? text = null) =>
        Run(new AnnotationSetOperation(Services), new AnnotationSetRequest { Annotation = annotation, At = at, Text = text });

    AnnotationDeleteResult Delete(params string[] annotations) =>
        Run(new AnnotationDeleteOperation(Services), new AnnotationDeleteRequest { Annotations = annotations.ToList() });

    [Fact]
    public void The_list_reads_every_annotation_or_a_sheets_or_a_views()
    {
        var all = List().Annotations;
        Assert.Equal(4, all.Count);
        Assert.Equal(new[] { _note, _depth, _diameter, "DetailItem" }, all.Select(a => a.Name.StartsWith("DetailItem") && a.Sheet == "Sheet2" ? "DetailItem" : a.Name));
        Assert.True(all.Single(a => a.Name == _depth).ModelDimension);
        Assert.Equal("D1@Boss-Extrude1", all.Single(a => a.Name == _depth).DimensionName);
        Assert.Equal(10, all.Single(a => a.Name == _diameter).Value!.Value, 9);
        Assert.Equal(3, List(sheet: "Sheet1").Annotations.Count);
        Assert.Equal(2, List(view: Front.Facts.Name).Annotations.Count);
        Assert.Equal("/view", Fails(() => List(sheet: "Sheet2", view: Front.Facts.Name)).Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_note_moves_and_takes_new_text_by_name_or_reference()
    {
        var moved = Set(_note, new SheetPoint(310, 40));
        Assert.Equal(310, moved.Annotation.Position!.X, 9);
        Assert.Equal("position", Assert.Single(moved.Change.Changed).Property);
        var renamed = Set(moved.Annotation.Reference!, text: "TITLE: $PRP:\"Title\"");
        Assert.Equal("TITLE: $PRP:\"Title\"", renamed.Annotation.LinkedText);
    }

    [Fact]
    public void Text_is_for_notes_and_a_set_with_nothing_is_refused()
    {
        Assert.Equal("/text", Fails(() => Set(_diameter, text: "12")).Error.Details!["field"]);
        Assert.Equal("/annotation", Fails(() => Set(_note)).Error.Details!["field"]);
        Assert.Equal("/at", Fails(() => Set(_note, new SheetPoint(-1, 40))).Error.Details!["field"]);
    }

    [Fact]
    public void A_move_SOLIDWORKS_ignores_is_refused()
    {
        Drw.AnnotationSetIgnored = true;
        var e = Fails(() => Set(_note, new SheetPoint(310, 40)));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("position", e.Error.Details!["check"]);
    }

    [Fact]
    public void Deleting_the_imported_depth_leaves_it_in_the_model_and_everything_else_on_the_drawing()
    {
        var r = Delete(_depth, _note);
        Assert.Equal(new[] { $"{_depth}@{Front.Facts.Name}", $"{_note}@Sheet1" }, r.Deleted);
        Assert.Equal("D1@Boss-Extrude1 = 20", Assert.Single(r.ModelDimensionsKept));
        Assert.Equal((4, 2), (r.Change.AnnotationsBefore, r.Change.AnnotationsAfter));
        Assert.Equal(2, r.Change.Changed.Count(c => c.Property == "deleted"));
        Assert.Single(Block.FeatureDimensions);
    }

    [Fact]
    public void Annotations_SOLIDWORKS_leaves_are_refused_and_said_to_be_there()
    {
        Drw.AnnotationDeleteLeaves = true;
        var e = Fails(() => Delete(_diameter));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("is still there", e.Message);
    }

    [Fact]
    public void A_delete_SOLIDWORKS_refuses_after_another_views_went_is_rollback_failed()
    {
        Drw.AnnotationDeleteFailsIn = Front.Facts.Name;
        var e = Fails(() => Delete(_note, _diameter));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Null(DrawingAnnotations.Read(Drw, "Sheet1", null, _note));
        Assert.NotNull(DrawingAnnotations.Read(Drw, "Sheet1", Front.Facts.Name, _diameter));
    }

    [Fact]
    public void An_annotation_named_twice_or_unknown_is_refused_and_a_deleted_ones_reference_no_longer_resolves()
    {
        Assert.Equal("/annotations/1", Fails(() => Delete(_note, _note)).Error.Details!["field"]);
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Delete("Note99")).Code);
        string reference = List().Annotations.Single(a => a.Name == _note).Reference!;
        Delete(_note);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Set(reference, new SheetPoint(310, 40))).Code);
    }
}
