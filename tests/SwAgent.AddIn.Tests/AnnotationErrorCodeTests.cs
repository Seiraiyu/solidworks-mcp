using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Design 611, Error handling: every code each annotation operation and drawing.properties defines is reachable.</summary>
public class AnnotationErrorCodeTests : AnnotationTestBase
{
    readonly FakeView _top;
    readonly FakeEntity _half;
    readonly string _note;

    public AnnotationErrorCodeTests()
    {
        var boss = Block.AddFeature("Boss-Extrude1", "Extrusion");
        Block.AddFeatureDimension(boss, "D1@Boss-Extrude1", 20, angle: false);
        Drw.ImportPlan.Add((Front.Facts.Name, AnnotationKind.Dimension, "D1@Boss-Extrude1"));
        _top = Drw.AddView("Sheet1", Block, "top", new SheetPoint(100, 60));
        _half = Block.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        _top.Faces.Add(_half);
        Drw.AddAnnotation(new AnnotationFacts { Name = "DatumTag90", Sheet = "Sheet1", View = Front.Facts.Name, Kind = AnnotationKind.Datum, Label = "A", Attached = new List<object> { Bottom } });
        _note = Drw.AddNote("Sheet1", null, "NOTE", new SheetPoint(300, 30), null)!;
        Drw.Calls.Clear();
    }

    /// <summary>One request of every annotation operation and of drawing.properties, on <paramref name="document"/> (the active document when null).</summary>
    IEnumerable<(string Name, Action Call)> Every(string? document) => new (string, Action)[]
    {
        ("annotation.import", () => Run(new AnnotationImportOperation(Services), new AnnotationImportRequest { Document = document })),
        ("annotation.dimension", () => Run(new AnnotationDimensionOperation(Services), new AnnotationDimensionRequest { Document = document, Kind = DimensionKind.Diameter, Entities = { On(Hole) }, At = new SheetPoint(120, 190) })),
        ("annotation.note", () => Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest { Document = document, Text = "THRU", At = new SheetPoint(130, 190), AttachTo = On(Hole) })),
        ("annotation.centre_marks", () => Run(new AnnotationCentreMarksOperation(Services), new AnnotationCentreMarksRequest { Document = document, View = Front.Facts.Name })),
        ("annotation.centre_lines", () => Run(new AnnotationCentreLinesOperation(Services), new AnnotationCentreLinesRequest { Document = document, View = _top.Facts.Name })),
        ("annotation.datum", () => Run(new AnnotationDatumOperation(Services), new AnnotationDatumRequest { Document = document, Label = "B", AttachTo = On(Left) })),
        ("annotation.gtol", () => Run(new AnnotationGtolOperation(Services), new AnnotationGtolRequest { Document = document, Characteristic = GtolCharacteristic.Position, ToleranceMm = 0.1, AttachTo = On(Hole), Datums = new List<string> { "A" }, Diameter = true })),
        ("annotation.list", () => Run(new AnnotationListOperation(Services), new AnnotationListRequest { Document = document })),
        ("annotation.set", () => Run(new AnnotationSetOperation(Services), new AnnotationSetRequest { Document = document, Annotation = _note, At = new SheetPoint(310, 40) })),
        ("annotation.delete", () => Run(new AnnotationDeleteOperation(Services), new AnnotationDeleteRequest { Document = document, Annotations = { _note } })),
        ("drawing.properties", () => Run(new DrawingPropertiesOperation(Services), new DrawingPropertiesRequest { Document = document, Set = new List<DrawingPropertyValue> { new DrawingPropertyValue { Name = "Title", Value = "BLOCK" } } })),
    };

    Action Call(string name) => Every(null).Single(c => c.Name == name).Call;

    [Fact]
    public void Wrong_document_type_for_every_annotation_operation_on_a_part_naming_the_drawing_tools()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        foreach (var (name, call) in Every("plate.SLDPRT"))
        {
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.WrongDocumentType, $"{name}: {e.Code}");
            Assert.Contains("document.new", (string)e.Error.Details!["instead"]!);
        }
    }

    [Fact]
    public void Busy_for_every_change_while_the_user_edits_the_sheet_format_and_the_list_still_reads()
    {
        Drw.EditingFormat = true;
        foreach (var (name, call) in Every(null).Where(c => c.Name != "annotation.list"))
        {
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.Busy, $"{name}: {e.Code}");
            Assert.Equal(true, e.Error.Details!["sheet_format"]);
        }
        Assert.Empty(Drw.Calls);
        Assert.Equal(2, Run(new AnnotationListOperation(Services), new AnnotationListRequest()).Annotations.Count);
    }

    [Fact]
    public void Reference_unresolved_for_an_annotation_or_a_view_that_is_gone()
    {
        string note = Run(new AnnotationListOperation(Services), new AnnotationListRequest()).Annotations.Single(a => a.Name == _note).Reference!;
        Drw.DeleteAnnotations("Sheet1", null, new[] { _note });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationSetOperation(Services), new AnnotationSetRequest { Annotation = note, At = new SheetPoint(310, 40) })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationDeleteOperation(Services), new AnnotationDeleteRequest { Annotations = { note } })).Code);
        string view = Services.References.IssueView(Doc, Drw.ReadView(_top.Facts.Name)!);
        Drw.DeleteViews(new[] { _top.Facts.Name });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationCentreLinesOperation(Services), new AnnotationCentreLinesRequest { View = view })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationListOperation(Services), new AnnotationListRequest { View = view })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationDatumOperation(Services), new AnnotationDatumRequest { Label = "B", AttachTo = new ViewEntity { View = view, Entity = "Boss-Extrude1" } })).Code);
    }

    [Fact]
    public void Operation_failed_when_SOLIDWORKS_makes_deletes_or_writes_nothing()
    {
        Drw.AnnotateFails = true;
        Drw.AnnotationDeleteFails = true;
        Drw.PropertyRefused = "Title";
        foreach (var name in new[] { "annotation.dimension", "annotation.note", "annotation.centre_marks", "annotation.centre_lines", "annotation.datum", "annotation.gtol", "annotation.delete", "drawing.properties" })
        {
            var e = Fails(Call(name));
            Assert.True(e.Code == ErrorCode.OperationFailed, $"{name}: {e.Code}");
        }
    }

    [Theory]
    [InlineData("annotation.import")]
    [InlineData("annotation.dimension")]
    [InlineData("annotation.note")]
    [InlineData("annotation.centre_marks")]
    [InlineData("annotation.centre_lines")]
    [InlineData("annotation.datum")]
    [InlineData("annotation.gtol")]
    [InlineData("annotation.set")]
    [InlineData("annotation.delete")]
    [InlineData("drawing.properties")]
    public void Verification_failed_when_SOLIDWORKS_answers_but_the_drawing_says_otherwise(string name)
    {
        switch (name)
        {
            case "annotation.import": Drw.ImportedOffBy = 0.01; break;
            case "annotation.dimension": Drw.DimensionOffBy = 0.01; break;
            case "annotation.note":
            case "annotation.centre_marks": Drw.AnnotationLandsOffByMm = 0.02; break;
            case "annotation.set": Drw.AnnotationSetIgnored = true; break;
            case "annotation.delete": Drw.AnnotationDeleteLeaves = true; break;
            case "drawing.properties": Drw.PropertiesIgnored = true; break;
            default: Drw.AttachIgnored = true; break;
        }
        var e = Fails(Call(name));
        Assert.True(e.Code == ErrorCode.VerificationFailed, $"{name}: {e.Code}");
    }

    [Theory]
    [InlineData("annotation.dimension", "/entities/0")]
    [InlineData("annotation.note", "/attach_to")]
    [InlineData("annotation.centre_marks", "/entities/0")]
    [InlineData("annotation.datum", "/attach_to")]
    [InlineData("annotation.gtol", "/attach_to")]
    public void Invalid_argument_naming_the_view_for_an_entity_it_does_not_show(string name, string field)
    {
        var back = On(Back);
        Action call = name switch
        {
            "annotation.dimension" => () => Run(new AnnotationDimensionOperation(Services), new AnnotationDimensionRequest { Kind = DimensionKind.Linear, Entities = { back }, At = new SheetPoint(120, 190) }),
            "annotation.note" => () => Run(new AnnotationNoteOperation(Services), new AnnotationNoteRequest { Text = "X", At = new SheetPoint(130, 190), AttachTo = back }),
            "annotation.centre_marks" => () => Run(new AnnotationCentreMarksOperation(Services), new AnnotationCentreMarksRequest { View = Front.Facts.Name, Entities = new List<ViewEntity> { back } }),
            "annotation.datum" => () => Run(new AnnotationDatumOperation(Services), new AnnotationDatumRequest { Label = "B", AttachTo = back }),
            _ => () => Run(new AnnotationGtolOperation(Services), new AnnotationGtolRequest { Characteristic = GtolCharacteristic.Flatness, ToleranceMm = 0.1, AttachTo = back }),
        };
        var e = Fails(call);
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Equal(Front.Facts.Name, e.Error.Details["view"]);
    }
}
