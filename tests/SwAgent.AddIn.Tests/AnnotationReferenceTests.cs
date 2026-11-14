using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

[Operation("test.annotation_change", typeof(TestAnnotationChangeResult))]
public sealed class TestAnnotationChangeRequest
{
    /// <summary>Where to fail: change or check.</summary>
    public string? FailIn { get; set; }
}

public sealed class TestAnnotationChangeResult : IDrawingChangeResult
{
    public DrawingChange Change { get; set; } = new DrawingChange();
}

/// <summary>Adds a note to Sheet1 and fails where it is told to.</summary>
sealed class TestAnnotationChangeOperation : DrawingChangeOperation<TestAnnotationChangeRequest, TestAnnotationChangeResult>
{
    public TestAnnotationChangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(TestAnnotationChangeRequest request) => null;

    protected override TestAnnotationChangeResult Change(ISwDocument doc, ISwDrawing drawing, TestAnnotationChangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        string name = drawing.AddNote("Sheet1", null, "NOTE", new SheetPoint(300, 30), null)!;
        MadeAnnotation(doc, drawing, tx, DrawingAnnotations.Read(drawing, "Sheet1", null, name)!);
        if (request.FailIn == "change") throw new InvalidOperationException("COM said no");
        return new TestAnnotationChangeResult();
    }

    protected override void Check(ISwDocument doc, ISwDrawing drawing, TestAnnotationChangeRequest request, TestAnnotationChangeResult result)
    {
        if (request.FailIn == "check") throw new SwAgentException(ErrorCode.VerificationFailed, "the note is 3 mm off");
    }
}

/// <summary>The block's front view at 1:1 centred at (100, 150), with a note on Sheet1 and a reference dimension and a model dimension in the front view.</summary>
public class AnnotationReferenceTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly FakeEntity _left;

    public AnnotationReferenceTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        _left = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        _front.Visible.Add(new VisibleEntity(_left, false, false));
        Drw.AddAnnotation(new AnnotationFacts { Name = "DetailItem100", Sheet = "Sheet1", Kind = AnnotationKind.Note, Text = "NOTE", LinkedText = "NOTE" });
        Drw.AddAnnotation(new AnnotationFacts { Name = "RD1@Drawing View1", Sheet = "Sheet1", View = "Drawing View1", Kind = AnnotationKind.Dimension, DimensionName = "RD1@Drawing View1", Value = 40, Driven = true, Attached = new List<object> { _left } });
        Drw.AddAnnotation(new AnnotationFacts { Name = "D1@Boss-Extrude1@Drawing View1", Sheet = "Sheet1", View = "Drawing View1", Kind = AnnotationKind.Dimension, DimensionName = "D1@Boss-Extrude1@block.Part", Value = 20 });
    }

    AnnotationFacts Resolve(string text) => Services.References.ResolveAnnotation(Doc, Drw, text, "/annotation");

    [Fact]
    public void An_annotation_reference_names_its_sheet_view_and_name_and_resolves()
    {
        var note = DrawingAnnotations.Read(Drw, "Sheet1", null, "DetailItem100")!;
        string reference = Services.References.IssueAnnotation(Doc, note);
        Assert.StartsWith("r1.annotation.", reference);
        Assert.Equal("DetailItem100", Resolve(reference).Name);
        Assert.Equal("note DetailItem100 in sheet Sheet1", References.Decode(reference, "/x").Description);
    }

    [Fact]
    public void A_name_resolves_alone_or_with_its_view()
    {
        Assert.Equal("Drawing View1", Resolve("RD1@Drawing View1").View);
        Assert.Null(Resolve("DetailItem100").View);
        Drw.AddAnnotation(new AnnotationFacts { Name = "DetailItem100", Sheet = "Sheet1", View = "Drawing View1", Kind = AnnotationKind.Note });
        var e = Fails(() => Resolve("DetailItem100"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("names 2 annotations", e.Message);
        Assert.Equal("Drawing View1", Resolve("DetailItem100@Drawing View1").View);
        Assert.Null(Resolve("DetailItem100@Sheet1").View);
    }

    [Fact]
    public void A_deleted_annotations_reference_is_unresolved_and_an_unknown_name_invalid()
    {
        string reference = Services.References.IssueAnnotation(Doc, DrawingAnnotations.Read(Drw, "Sheet1", null, "DetailItem100")!);
        Drw.DeleteAnnotations("Sheet1", null, new[] { "DetailItem100" });
        var e = Fails(() => Resolve(reference));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("not_found", e.Error.Details!["reason"]);
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Resolve("Note9")).Code);
        string view = Services.References.IssueView(Doc, Drw.ReadView("Drawing View1")!);
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Resolve(view)).Code);
    }

    [Fact]
    public void A_reference_dimension_is_the_drawings_and_a_model_dimension_is_named_as_the_model_names_it()
    {
        var all = DrawingAnnotations.All(Drw).Select(a => DrawingAnnotations.Info(Services.References, Doc, Drw, a)).ToList();
        var reference = all.Single(a => a.Name == "RD1@Drawing View1");
        Assert.False(reference.ModelDimension);
        Assert.Equal("RD1@Drawing View1", reference.DimensionName);
        Assert.Equal("Drawing View1", Assert.Single(reference.Attached).View);
        Assert.StartsWith("r1.edge.", reference.Attached[0].Reference);
        var model = all.Single(a => a.Value == 20);
        Assert.True(model.ModelDimension);
        Assert.Equal("D1@Boss-Extrude1", model.DimensionName);
        Assert.Null(all.Single(a => a.Kind == AnnotationKind.Note).ModelDimension);
    }

    [Fact]
    public void An_entity_the_view_does_not_show_is_refused_naming_the_view_and_the_entity()
    {
        var back = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 0), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        string reference = Services.References.Describe(Block, back).Reference!;
        var e = Fails(() => ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = "Drawing View1", Reference = reference }, "/attach_to"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("Drawing View1", e.Error.Details!["view"]);
        Assert.Equal("/attach_to", e.Error.Details["field"]);
        string shown = Services.References.Describe(Block, _left).Reference!;
        Assert.Same(_left, ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = "Drawing View1", Reference = shown }, "/attach_to").Entity);
    }

    [Fact]
    public void A_reference_the_model_no_longer_finds_is_unresolved_even_when_the_view_shows_an_entity_with_its_id()
    {
        // Never matched among what the view shows: SP4 gives the view's entities twin ids (5.C live run, round 2).
        string shown = Services.References.Describe(Block, _left).Reference!;
        _left.State = PersistState.Invalid;
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = "Drawing View1", Reference = shown }, "/attach_to")).Code);
    }

    [Fact]
    public void A_change_counts_annotations_before_and_after_and_reports_what_it_made()
    {
        var c = Run(new TestAnnotationChangeOperation(Services), new TestAnnotationChangeRequest()).Change;
        Assert.Equal((3, 4), (c.AnnotationsBefore, c.AnnotationsAfter));
        var made = Assert.Single(c.Created);
        Assert.Equal(ReferenceKind.Annotation, made.Kind);
        Assert.StartsWith("r1.annotation.", made.Reference);
    }

    [Theory]
    [InlineData("change", ErrorCode.OperationFailed)]
    [InlineData("check", ErrorCode.VerificationFailed)]
    public void A_failure_deletes_the_annotation_it_made(string failIn, ErrorCode code)
    {
        var e = Fails(() => Run(new TestAnnotationChangeOperation(Services), new TestAnnotationChangeRequest { FailIn = failIn }));
        Assert.Equal(code, e.Code);
        Assert.Equal(3, DrawingAnnotations.Count(Drw));
        Assert.Contains(Drw.Calls, c => c.StartsWith("delete annotations DetailItem", StringComparison.Ordinal));
    }

    [Fact]
    public void An_annotation_SOLIDWORKS_would_not_delete_is_rollback_failed()
    {
        Drw.AnnotationDeleteFails = true;
        var e = Fails(() => Run(new TestAnnotationChangeOperation(Services), new TestAnnotationChangeRequest { FailIn = "check" }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Equal(4, DrawingAnnotations.Count(Drw));
    }
}
