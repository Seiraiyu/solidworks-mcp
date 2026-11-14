using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DrawingReferenceTests
{
    readonly RenameMap _renames = new RenameMap();
    readonly ReferenceService _refs;
    readonly FakeDocument _drawing = FakeDocument.NewDrawing("block.SLDDRW", @"C:\d\block.SLDDRW");
    readonly FakeDocument _block = FakeDocument.Part("block.SLDPRT", @"C:\d\block.SLDPRT");
    readonly FakeView _front;

    public DrawingReferenceTests()
    {
        _refs = new ReferenceService(_renames);
        _block.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        _front = _drawing.Drw!.AddView("Sheet1", _block, "front", new SheetPoint(100, 150));
    }

    FakeDrawing Drw => _drawing.Drw!;

    static SwAgentException Fails(System.Action action) => Assert.Throws<SwAgentException>(action);

    [Fact]
    public void A_sheet_reference_carries_its_name_and_resolves_to_it()
    {
        string reference = _refs.IssueSheet(_drawing, "Sheet1");
        Assert.StartsWith("r1.sheet.", reference);
        Assert.Equal("sheet Sheet1", References.Decode(reference, "sheet").Description);
        Assert.Equal("Sheet1", _refs.ResolveSheet(_drawing, Drw, reference, "sheet"));
    }

    [Fact]
    public void A_view_reference_is_described_by_its_name_sheet_model_and_orientation()
    {
        string reference = _refs.IssueView(_drawing, Drw.ReadView(_front.Facts.Name)!);
        Assert.StartsWith("r1.view.", reference);
        Assert.Equal("view Drawing View1 on Sheet1, block.SLDPRT, front", References.Decode(reference, "view").Description);
        Assert.Equal("Drawing View1", _refs.ResolveView(_drawing, Drw, reference, "view"));
    }

    [Fact]
    public void Names_resolve_exactly_or_by_a_unique_case_blind_match()
    {
        Assert.Equal("Sheet1", _refs.ResolveSheet(_drawing, Drw, "sheet1", "sheet"));
        Assert.Equal("Drawing View1", _refs.ResolveView(_drawing, Drw, "Drawing View1", "view"));
        var e = Fails(() => _refs.ResolveView(_drawing, Drw, "Drawing View9", "/views/0"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/views/0", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { "Drawing View1" }, (List<object?>)e.Error.Details["views"]!);
    }

    [Fact]
    public void A_deleted_or_renamed_view_is_unresolved_and_never_substituted()
    {
        string reference = _refs.IssueView(_drawing, Drw.ReadView(_front.Facts.Name)!);
        Drw.DeleteViews(new[] { _front.Facts.Name });
        Drw.AddView("Sheet1", _block, "front", new SheetPoint(100, 150));
        var e = Fails(() => _refs.ResolveView(_drawing, Drw, reference, "view"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("not_found", e.Error.Details!["reason"]);
        Assert.Contains("drawing.read", e.Message);
    }

    [Fact]
    public void A_reference_of_another_kind_or_drawing_is_refused()
    {
        string sheet = _refs.IssueSheet(_drawing, "Sheet1");
        var e = Fails(() => _refs.ResolveView(_drawing, Drw, sheet, "view"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("sheet", e.Error.Details!["kind"]);
        var other = FakeDocument.NewDrawing("other.SLDDRW", @"C:\d\other.SLDDRW");
        e = Fails(() => _refs.ResolveSheet(other, other.Drw!, sheet, "sheet"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("other_document", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_drawing_reference_given_for_a_model_entity_names_the_kinds_accepted()
    {
        string view = _refs.IssueView(_drawing, Drw.ReadView(_front.Facts.Name)!);
        var e = Fails(() => _refs.Resolve(_block, view, "edges/0", ReferenceKind.Edge, ReferenceKind.Face));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("view", e.Error.Details!["kind"]);
        Assert.Equal(new object?[] { "edge", "face" }, (List<object?>)e.Error.Details["accepted"]!);
        e = Fails(() => _refs.Resolve(_block, view, "on"));
        Assert.Contains("face", (List<object?>)e.Error.Details!["accepted"]!);
        Assert.DoesNotContain("sheet", (List<object?>)e.Error.Details["accepted"]!);
    }
}

public class ViewEntityTests
{
    readonly ReferenceService _refs = new ReferenceService(new RenameMap());
    readonly FakeDocument _drawing = FakeDocument.NewDrawing("block.SLDDRW", @"C:\d\block.SLDDRW");
    readonly FakeDocument _block = FakeDocument.Part("block.SLDPRT", @"C:\d\block.SLDPRT");
    readonly FakeEntity _hole;

    public ViewEntityTests()
    {
        _block.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        _drawing.Drw!.AddView("Sheet1", _block, "front", new SheetPoint(100, 150));
        _hole = _block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), RadiusMm = 5, Feature = "Cut-Extrude1" });
        _block.AddFeature("Cut-Extrude1", "Cut");
    }

    ResolvedViewEntity Resolve(ViewEntity given) => ViewEntities.Resolve(_refs, _drawing, _drawing.Drw!, given, "/attach_to");

    [Fact]
    public void A_reference_issued_in_the_views_model_resolves_there()
    {
        string reference = _refs.Describe(_block, _hole).Reference!;
        var resolved = Resolve(new ViewEntity { View = "Drawing View1", Reference = reference });
        Assert.Same(_hole, resolved.Entity);
        Assert.Same(_block, resolved.Model);
        Assert.Equal("Drawing View1", resolved.View);
    }

    [Fact]
    public void A_name_resolves_in_the_views_model()
    {
        var resolved = Resolve(new ViewEntity { View = "Drawing View1", Entity = "Cut-Extrude1" });
        Assert.Equal(ReferenceKind.Feature, resolved.Kind);
    }

    [Theory]
    [InlineData("both", "/attach_to/reference")]
    [InlineData("neither", "/attach_to/entity")]
    [InlineData("component with reference", "/attach_to/component")]
    [InlineData("unknown view", "/attach_to/view")]
    [InlineData("component in a part", "/attach_to/component")]
    public void Exactly_one_entity_form_is_taken_and_each_mistake_names_its_field(string what, string field)
    {
        string reference = _refs.Describe(_block, _hole).Reference!;
        var given = what switch
        {
            "both" => new ViewEntity { View = "Drawing View1", Reference = reference, Entity = "Cut-Extrude1" },
            "neither" => new ViewEntity { View = "Drawing View1" },
            "component with reference" => new ViewEntity { View = "Drawing View1", Reference = reference, Component = "pin-1" },
            "unknown view" => new ViewEntity { View = "Drawing View7", Entity = "Cut-Extrude1" },
            _ => new ViewEntity { View = "Drawing View1", Component = "pin-1", Entity = "Axis1" },
        };
        var e = Assert.Throws<SwAgentException>(() => Resolve(given));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }
}
