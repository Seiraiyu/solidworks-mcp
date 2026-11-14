using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ViewProjectTests : DrawingTestBase
{
    readonly FakeView _front;

    public ViewProjectTests()
    {
        // The block's front view at 1:1 centred at (100, 150): drawn 70..130 by 130..170, 20 deep.
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
    }

    ViewResult Project(ProjectDirection direction, SheetPoint? at = null) =>
        Run(new ViewProjectOperation(Services), new ViewProjectRequest { Parent = _front.Facts.Name, Direction = direction, At = at });

    [Fact]
    public void A_view_from_the_right_goes_right_in_third_angle_on_the_centre_line_with_a_gap()
    {
        var v = Project(ProjectDirection.Right).View;
        Assert.Equal(ViewType.Projected, v.Type);
        Assert.Equal(_front.Facts.Name, v.Parent);
        Assert.Equal(160, v.Position.X, 9);
        Assert.Equal(150, v.Position.Y, 9);
        Assert.Equal(150, v.Extents!.Min.X, 9);
        Assert.Equal(170, v.Extents.Max.X, 9);
        Assert.Equal(130, v.Extents.Min.Y, 9);
    }

    [Fact]
    public void A_view_from_above_goes_below_in_first_angle()
    {
        var sheet = Drw.Sheet("Sheet1");
        sheet.Facts.Projection = Projection.FirstAngle;
        var v = Project(ProjectDirection.Above).View;
        Assert.Equal(100, v.Position.X, 9);
        Assert.Equal(100, v.Position.Y, 9);
        Assert.Equal(20, v.Extents!.Max.Y - v.Extents.Min.Y, 9);
    }

    [Fact]
    public void A_position_on_the_centre_line_and_the_right_side_is_taken()
    {
        var v = Project(ProjectDirection.Left, new SheetPoint(30, 150)).View;
        Assert.Equal(30, v.Position.X, 9);
    }

    [Theory]
    [InlineData(250, 150, "/at")]
    [InlineData(200, 160, "/at/y")]
    [InlineData(500, 150, "/at")]
    public void A_position_off_the_side_or_the_centre_line_or_the_sheet_is_refused(double x, double y, string field)
    {
        var e = Fails(() => Project(x == 250 ? ProjectDirection.Left : ProjectDirection.Right, new SheetPoint(x, y)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void An_unknown_parent_is_refused_naming_the_field()
    {
        var e = Fails(() => Run(new ViewProjectOperation(Services), new ViewProjectRequest { Parent = "Drawing View9", Direction = ProjectDirection.Right }));
        Assert.Equal("/parent", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_view_off_its_parents_centre_line_is_deleted_again()
    {
        Drw.AfterCreate = v => v.Transform = new SwAgent.AddIn.Core.ViewTransform(v.Transform!.Rotation, v.Transform.TxMm, v.Transform.TyMm + 1, 0, v.Transform.Scale);
        var e = Fails(() => Project(ProjectDirection.Right));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("alignment", e.Error.Details!["check"]);
        Assert.Contains("horizontal centre line", (string)e.Error.Details["measured"]!);
        Assert.Single(Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void SOLIDWORKS_making_no_view_is_operation_failed()
    {
        Drw.CreateFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Project(ProjectDirection.Below)).Code);
    }
}

public class ViewSetTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly string _right;

    public ViewSetTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        _right = Drw.ProjectView(_front.Facts.Name, new SheetPoint(160, 150))!;
        Drw.Calls.Clear();
    }

    ViewResult Set(ViewSetRequest request) => Run(new ViewSetOperation(Services), request);

    [Fact]
    public void A_model_view_moves_and_its_projected_view_follows_on_its_centre_line()
    {
        var result = Set(new ViewSetRequest { View = _front.Facts.Name, At = new SheetPoint(120, 200) });
        Assert.Equal(200, result.View.Position.Y, 9);
        Assert.Equal(200, Drw.ReadView(_right)!.Position.Y, 9);
        var changed = result.Change.Changed.Single();
        Assert.Equal("position", changed.Property);
        Assert.Equal("(100, 150)", changed.Before);
    }

    [Fact]
    public void A_projected_view_moves_with_its_parent_the_whole_way_as_SOLIDWORKS_drags_it()
    {
        // 5.A live run: the front moved 10 mm along x and its right view, aligned along y, moved 10 mm along x too.
        Set(new ViewSetRequest { View = _front.Facts.Name, At = new SheetPoint(110, 150) });
        var right = Drw.ReadView(_right)!;
        Assert.Equal(170, right.Position.X, 9);
        Assert.Equal(150, right.Position.Y, 9);
    }

    [Fact]
    public void A_projected_view_moves_along_its_alignment_and_not_off_it()
    {
        Assert.Equal(250, Set(new ViewSetRequest { View = _right, At = new SheetPoint(250, 150) }).View.Position.X, 9);
        var e = Fails(() => Set(new ViewSetRequest { View = _right, At = new SheetPoint(250, 100) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/at/y", e.Error.Details!["field"]);
        Assert.Contains(_front.Facts.Name, e.Message);
    }

    [Fact]
    public void A_view_is_rescaled_about_its_centre_and_drawn_at_the_new_scale()
    {
        var v = Set(new ViewSetRequest { View = _front.Facts.Name, Scale = new ScaleRatio(2, 1) }).View;
        Assert.Equal(120, v.Extents!.Max.X - v.Extents.Min.X, 9);
        Assert.Equal(100, v.Position.X, 9);
        Assert.False(v.SheetScale);
        var back = Set(new ViewSetRequest { View = _front.Facts.Name, SheetScale = true }).View;
        Assert.True(back.SheetScale);
        Assert.Equal(1, back.Scale.Numerator);
    }

    [Fact]
    public void Display_and_configuration_are_set_and_recorded()
    {
        Drw.Configurations[Block.Document.Path!] = new List<string> { "Default", "Long" };
        var result = Set(new ViewSetRequest { View = _front.Facts.Name, Display = ViewDisplay.Wireframe, Configuration = "Long" });
        Assert.Equal(ViewDisplay.Wireframe, result.View.Display);
        Assert.Equal("Long", result.View.Configuration);
        Assert.Equal(new[] { "configuration", "display" }, result.Change.Changed.Select(c => c.Property));
    }

    [Theory]
    [InlineData("nothing", "/view")]
    [InlineData("both scales", "/sheet_scale")]
    [InlineData("projected scale", "/scale")]
    [InlineData("projected configuration", "/configuration")]
    [InlineData("off the sheet", "/at")]
    public void Bad_requests_are_refused_naming_the_field(string what, string field)
    {
        var request = what switch
        {
            "nothing" => new ViewSetRequest { View = _front.Facts.Name },
            "both scales" => new ViewSetRequest { View = _front.Facts.Name, Scale = new ScaleRatio(1, 2), SheetScale = true },
            "projected scale" => new ViewSetRequest { View = _right, Scale = new ScaleRatio(1, 2) },
            "projected configuration" => new ViewSetRequest { View = _right, Configuration = "Long" },
            _ => new ViewSetRequest { View = _front.Facts.Name, At = new SheetPoint(100, -5) },
        };
        var e = Fails(() => Set(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_value_that_does_not_take_is_put_back_and_fails_verification()
    {
        Drw.ViewSetIgnored = true;
        var e = Fails(() => Set(new ViewSetRequest { View = _front.Facts.Name, Display = ViewDisplay.Shaded }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("display", e.Error.Details!["check"]);
        Assert.Equal(new object?[] { _front.Facts.Name }, (List<object?>)e.Error.Details["rolled_back"]!);
    }

    [Fact]
    public void A_move_that_breaks_a_childs_alignment_is_put_back()
    {
        var child = Drw.View(_right);
        Drw.AfterMove = moved => { if (moved == _front.Facts.Name) child.Transform = new SwAgent.AddIn.Core.ViewTransform(child.Transform!.Rotation, child.Transform.TxMm + 3, child.Transform.TyMm + 3, 0, 1); };
        var e = Fails(() => Set(new ViewSetRequest { View = _front.Facts.Name, At = new SheetPoint(110, 140) }));
        Assert.Equal("alignment", e.Error.Details!["check"]);
        Assert.Equal(100, Drw.ReadView(_front.Facts.Name)!.Position.X, 9);
    }
}

public class ViewDeleteTests : DrawingTestBase
{
    readonly string _front;
    readonly string _right;
    readonly string _iso;

    public ViewDeleteTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150)).Facts.Name;
        _right = Drw.ProjectView(_front, new SheetPoint(160, 150))!;
        _iso = Drw.AddView("Sheet1", Block, "isometric", new SheetPoint(300, 200)).Facts.Name;
        Drw.Calls.Clear();
    }

    ViewDeleteResult Delete(params string[] views) => Run(new ViewDeleteOperation(Services), new ViewDeleteRequest { Views = views.ToList() });

    [Fact]
    public void Deletes_views_by_name_or_reference_and_a_stale_reference_is_unresolved()
    {
        string reference = Services.References.IssueView(Doc, Drw.ReadView(_iso)!);
        var result = Delete(reference);
        Assert.Equal(new[] { _iso }, result.Deleted);
        Assert.Equal((3, 2), (result.Change.ViewsBefore, result.Change.ViewsAfter));
        var e = Fails(() => Delete(reference));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("not_found", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_view_others_were_made_from_is_refused_unless_they_go_too()
    {
        var e = Fails(() => Delete(_front));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/views/0", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { _right }, (List<object?>)e.Error.Details["dependents"]!);
        Assert.Empty(Drw.Calls);
        Assert.Equal(new[] { _front, _right }, Delete(_front, _right).Deleted);
        Assert.Equal(new[] { _iso }, Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void A_view_SOLIDWORKS_will_not_delete_is_operation_failed()
    {
        Drw.DeleteFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Delete(_iso)).Code);
    }

    [Theory]
    [InlineData("twice", "/views/1")]
    [InlineData("unknown", "/views/1")]
    [InlineData("none", "/views")]
    public void Bad_lists_are_refused_naming_the_field(string what, string field)
    {
        var e = what switch
        {
            "twice" => Fails(() => Delete(_iso, _iso)),
            "unknown" => Fails(() => Delete(_iso, "Drawing View9")),
            _ => Fails(() => Delete()),
        };
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }
}

public class ViewEntitiesTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly FakeEntity _hole;

    public ViewEntitiesTests()
    {
        // The block's front view at 1:1 centred at (100, 150): its origin is drawn at (70, 130).
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        _hole = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 2 * System.Math.PI * 5, Feature = "Cut-Extrude1" });
        var top = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(30, 40, 20), Axis = new Vector3(1, 0, 0), LengthMm = 60, Feature = "Boss-Extrude1" });
        top.Id = null;
        var corner = Block.Add(ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(60, 40, 20) });
        _front.Visible.Add(new VisibleEntity(corner, vertex: true, silhouette: false));
        _front.Visible.Add(new VisibleEntity(_hole, vertex: false, silhouette: false));
        _front.Visible.Add(new VisibleEntity(top, vertex: false, silhouette: false));
    }

    ViewEntitiesResult Entities(ViewEntitiesRequest request) => Run(new ViewEntitiesOperation(Services), request);

    [Fact]
    public void Lists_edges_then_vertices_with_model_references_and_their_sheet_geometry()
    {
        var result = Entities(new ViewEntitiesRequest { View = _front.Facts.Name });
        Assert.Equal(Block.Document.Path, result.Model);
        Assert.Equal(1, result.Scale);
        Assert.Equal(new[] { ReferenceKind.Edge, ReferenceKind.Edge, ReferenceKind.Vertex }, result.Entities.Select(e => e.Kind));
        var hole = result.Entities[0];
        Assert.Equal(DrawnShape.Circle, hole.Shape);
        Assert.Equal(90, hole.Centre!.X, 9);
        Assert.Equal(150, hole.Centre.Y, 9);
        Assert.Equal(5, hole.RadiusMm!.Value, 9);
        Assert.Equal("Cut-Extrude1", hole.Entity!.Feature);
        var (entity, _) = Services.References.Resolve(Block, hole.Reference!, "edge");
        Assert.Same(_hole, entity);
        Assert.Null(result.Entities[1].Reference);
        Assert.Equal(DrawnShape.Line, result.Entities[1].Shape);
        Assert.Equal(130, result.Entities[2].Start!.X, 9);
    }

    [Fact]
    public void SOLIDWORKS_view_proxies_with_twin_ids_are_named_by_the_models_own_entities()
    {
        // The 5.C live run (round 2): SP4 gave the front view's top and bottom edges one reference and its left and right
        // edges another (GetPersistReference3 on IView.GetVisibleEntities2's entities), none of which the part resolved.
        var side = Drw.AddView("Sheet1", Block, "right", new SheetPoint(300, 150));
        var left = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        var right = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(60, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        var shownLeft = Drw.ShowAsProxy(side, left);
        var shownRight = Drw.ShowAsProxy(side, right);
        var result = Entities(new ViewEntitiesRequest { View = side.Facts.Name, Kinds = new List<ViewEntityKind> { ViewEntityKind.Edge } });
        var refs = result.Entities.Select(e => e.Reference).ToList();
        Assert.Equal(2, refs.Count(r => r != null));
        Assert.Equal(2, refs.Distinct().Count());
        var onLeft = Assert.Single(result.Entities, e => ReferenceEquals(left, Services.References.Resolve(Block, e.Reference!, "edge").Entity));
        Assert.Single(result.Entities, e => ReferenceEquals(right, Services.References.Resolve(Block, e.Reference!, "edge").Entity));
        var resolved = ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = side.Facts.Name, Reference = onLeft.Reference }, "/attach_to", ReferenceKind.Edge);
        Assert.Same(shownLeft, resolved.Entity);
        Assert.NotSame(shownRight, resolved.Entity);
    }

    [Fact]
    public void A_view_proxy_SOLIDWORKS_maps_to_nothing_is_named_by_the_one_entity_of_the_part_with_its_shape()
    {
        // The 5.C live run (round 3): SP4's correspondence verified none of the front view's proxies.
        var side = Drw.AddView("Sheet1", Block, "right", new SheetPoint(300, 150));
        var left = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        var shown = Drw.ShowAsProxy(side, left, corresponds: false);
        var result = Entities(new ViewEntitiesRequest { View = side.Facts.Name, Kinds = new List<ViewEntityKind> { ViewEntityKind.Edge } });
        string reference = Assert.Single(result.Entities).Reference!;
        Assert.Same(left, Services.References.Resolve(Block, reference, "edge").Entity);
        Assert.Same(shown, ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = side.Facts.Name, Reference = reference }, "/attach_to").Entity);
    }

    [Fact]
    public void View_proxies_whose_positions_SOLIDWORKS_gives_shifted_by_the_views_position_are_still_named_and_drawn_right()
    {
        // The 5.C live run (round 4): with the view activated, the front view's line and vertex proxies read shifted by
        // the view's position ((130, 180) mm) while its circle did not.
        var side = Drw.AddView("Sheet1", Block, "front", new SheetPoint(300, 150));
        var shift = new Point3(130, 180, 0);
        var left = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        var right = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(60, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        var bottom = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(30, 0, 20), Axis = new Vector3(1, 0, 0), LengthMm = 60 });
        var shownRight = Drw.ShowAsProxy(side, right, corresponds: false, shiftedBy: shift);
        foreach (var e in new[] { left, bottom }) Drw.ShowAsProxy(side, e, corresponds: false, shiftedBy: shift);
        Drw.ShowAsProxy(side, _hole, corresponds: false);
        var result = Entities(new ViewEntitiesRequest { View = side.Facts.Name, Kinds = new List<ViewEntityKind> { ViewEntityKind.Edge } });
        Assert.All(result.Entities, e => Assert.NotNull(e.Reference));
        Assert.Equal(4, result.Entities.Select(e => e.Reference).Distinct().Count());
        var onRight = Assert.Single(result.Entities, e => ReferenceEquals(right, Services.References.Resolve(Block, e.Reference!, "edge").Entity));
        var drawnRight = ViewGeometry.Draw(Drw.Transform(side.Facts.Name)!, Block.Describe(right), false);
        Assert.Equal(drawnRight.Start!.X, onRight.Start!.X, 9);
        Assert.Equal(drawnRight.Start.Y, onRight.Start.Y, 9);
        var resolved = ViewEntities.ResolveShown(Services.References, Doc, Drw, new ViewEntity { View = side.Facts.Name, Reference = onRight.Reference }, "/entities/0");
        Assert.Same(shownRight, resolved.Entity);
        Assert.Same(right, resolved.Own);
    }

    [Fact]
    public void A_view_proxy_whose_shape_the_part_has_twice_or_not_at_all_gets_no_reference()
    {
        var side = Drw.AddView("Sheet1", Block, "right", new SheetPoint(300, 150));
        var twin = new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 };
        var left = Block.Add(ReferenceKind.Edge, twin);
        Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 20, 20), Axis = new Vector3(0, 1, 0), LengthMm = 40 });
        Drw.ShowAsProxy(side, left, corresponds: false);
        var stray = new FakeEntity(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(99, 99, 99), Axis = new Vector3(1, 0, 0), LengthMm = 7 });
        Drw.ShowAsProxy(side, stray, corresponds: false);
        var result = Entities(new ViewEntitiesRequest { View = side.Facts.Name, Kinds = new List<ViewEntityKind> { ViewEntityKind.Edge } });
        Assert.Equal(2, result.Entities.Count);
        Assert.All(result.Entities, e => Assert.Null(e.Reference));
    }

    [Fact]
    public void Kinds_limit_what_is_listed()
    {
        var result = Entities(new ViewEntitiesRequest { View = _front.Facts.Name, Kinds = new List<ViewEntityKind> { ViewEntityKind.Vertex } });
        Assert.Equal(DrawnShape.Point, Assert.Single(result.Entities).Shape);
    }

    [Fact]
    public void An_assembly_view_is_refused_naming_the_view()
    {
        var asm = FakeDocument.NewAssembly("stand.SLDASM", PathOf("stand.SLDASM"));
        asm.ExactBox = new Box3 { Max = new Point3(10, 10, 10) };
        var view = Drw.AddView("Sheet1", asm, "front", new SheetPoint(300, 200));
        var e = Fails(() => Entities(new ViewEntitiesRequest { View = view.Facts.Name }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/view", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_view_that_changes_while_it_is_read_fails_verification()
    {
        var op = new ViewEntitiesOperation(Services);
        SwAgent.AddIn.Core.IOperation o = op;
        var request = new ViewEntitiesRequest { View = _front.Facts.Name };
        var result = (ViewEntitiesResult)o.Run(request, new TestContext());
        _front.Visible.RemoveAt(0);
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => o.Verify(request, result, new TestContext())).Code);
    }
}

public class ViewCreateTests : DrawingTestBase
{
    ViewResult Create(ViewCreateRequest request) => Run(new ViewCreateOperation(Services), request);

    ViewCreateRequest Front(double x = 100, double y = 150) => new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "front", At = new SheetPoint(x, y) };

    [Fact]
    public void A_front_view_of_the_block_is_placed_where_asked_and_draws_its_box_at_the_sheets_scale()
    {
        var result = Create(Front());
        var v = result.View;
        Assert.Equal("Drawing View1", v.Name);
        Assert.StartsWith("r1.view.", v.Reference);
        Assert.Equal("Sheet1", v.Sheet);
        Assert.Equal(ViewType.Model, v.Type);
        Assert.Equal("front", v.Orientation);
        Assert.True(v.SheetScale);
        Assert.Equal(ViewDisplay.HiddenLinesVisible, v.Display);
        Assert.Equal(70, v.Extents!.Min.X, 9);
        Assert.Equal(130, v.Extents.Max.X, 9);
        Assert.Equal(130, v.Extents.Min.Y, 9);
        Assert.Equal(170, v.Extents.Max.Y, 9);
        Assert.False(result.ModelUnsaved);
        Assert.Equal("Drawing View1", result.Change.Created.Single().Name);
        Assert.Equal(Block.Document.Path, result.Change.Models.Single().Path);
    }

    [Fact]
    public void Its_own_scale_display_and_configuration_are_taken()
    {
        Drw.Configurations[Block.Document.Path!] = new List<string> { "Default", "Long" };
        var request = Front();
        request.Scale = new ScaleRatio(1, 2);
        request.Display = ViewDisplay.ShadedWithEdges;
        request.Configuration = "Long";
        var v = Create(request).View;
        Assert.False(v.SheetScale);
        Assert.Equal(2, v.Scale.Denominator);
        Assert.Equal(30, v.Extents!.Max.X - v.Extents.Min.X, 9);
        Assert.Equal(ViewDisplay.ShadedWithEdges, v.Display);
        Assert.Equal("Long", v.Configuration);
    }

    [Fact]
    public void An_isometric_view_shows_hidden_lines_removed_and_lies_within_the_projected_box()
    {
        var v = Create(new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "Isometric", At = new SheetPoint(300, 200) }).View;
        Assert.Equal("isometric", v.Orientation);
        Assert.Equal(ViewDisplay.HiddenLinesRemoved, v.Display);
    }

    [Fact]
    public void A_view_goes_on_the_sheet_asked_which_is_shown_first()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Active = "Sheet1";
        var request = Front();
        request.Sheet = "Sheet2";
        Assert.Equal("Sheet2", Create(request).View.Sheet);
        Assert.Equal("Sheet2", Drw.Active);
    }

    [Fact]
    public void An_open_model_is_named_by_title_and_its_unsaved_changes_are_reported()
    {
        Sw.Track(Block);
        Block.Document.Dirty = true;
        var result = Create(new ViewCreateRequest { Model = "block.SLDPRT", Orientation = "top", At = new SheetPoint(100, 60) });
        Assert.True(result.ModelUnsaved);
        Assert.Equal("top", result.View.Orientation);
    }

    [Fact]
    public void A_never_saved_model_is_refused_before_SOLIDWORKS_is_called()
    {
        Sw.Track(FakeDocument.Part("Part7"));
        var e = Fails(() => Create(new ViewCreateRequest { Model = "Part7", Orientation = "front", At = new SheetPoint(100, 100) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/model", e.Error.Details!["field"]);
        Assert.Contains("sw_document_save_as", e.Message);
        Assert.Empty(Drw.Calls);
    }

    [Theory]
    [InlineData("missing file", "/model")]
    [InlineData("a drawing", "/model")]
    [InlineData("not open", "/model")]
    [InlineData("off the sheet", "/at")]
    [InlineData("zero scale", "/scale/denominator")]
    [InlineData("no orientation", "/orientation")]
    [InlineData("unknown sheet", "/sheet")]
    public void Bad_requests_are_refused_naming_the_field(string what, string field)
    {
        var request = Front();
        switch (what)
        {
            case "missing file": request.Model = PathOf("gone.SLDPRT"); break;
            case "a drawing": request.Model = Doc.Document.Path!; break;
            case "not open": request.Model = "plate"; break;
            case "off the sheet": request.At = new SheetPoint(430, 100); break;
            case "zero scale": request.Scale = new ScaleRatio(1, 0); break;
            case "no orientation": request.Orientation = " "; break;
            default: request.Sheet = "Sheet9"; break;
        }
        var e = Fails(() => Create(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void SOLIDWORKS_making_no_view_is_operation_failed()
    {
        Drw.CreateFails = true;
        var e = Fails(() => Create(new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "Shop view", At = new SheetPoint(100, 100) }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("no view of that name", e.Message);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("orientation")]
    [InlineData("extents")]
    [InlineData("configuration")]
    [InlineData("display")]
    public void A_view_that_is_not_as_asked_is_deleted_again(string check)
    {
        var request = Front();
        switch (check)
        {
            case "position": Drw.ViewSetIgnored = true; Drw.LandsOffByMm = 0.02; break;
            case "orientation": Drw.FacesInstead = "back"; break;
            case "extents": DrawsTooLittle(); break;
            case "configuration": request.Configuration = "Long"; break;
            default: Drw.ViewSetIgnored = true; request.Display = ViewDisplay.Shaded; break;
        }
        var e = Fails(() => Create(request));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "Drawing View1" }, (List<object?>)e.Error.Details["rolled_back"]!);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    /// <summary>The next view draws only half the block (an edge SOLIDWORKS left out).</summary>
    void DrawsTooLittle() => Drw.AfterCreate = v => v.Drawn = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 2, -0.03, -0.02, 0, 0, 0.02, 0 };

    [Fact]
    public void A_view_that_marks_its_model_changed_is_refused_and_deleted()
    {
        Sw.Track(Block);
        Drw.DirtiesModel = true;
        var e = Fails(() => Create(Front()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(Block);
        var request = Front();
        request.Document = Block.Document.Path;
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Create(request)).Code);
    }
}
