using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>A part with sketches to build features from: Sketch1 is a 40 x 20 rectangle on the front plane.</summary>
public abstract class FeatureTestBase : DocumentTestBase
{
    protected static Point2 P(double x, double y) => new Point2(x, y);

    internal FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        Sketch(new SketchGeometry { Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } } });
        return doc;
    }

    /// <summary>Draws a sketch on the front plane and returns its result.</summary>
    internal SketchCreateResult Sketch(SketchGeometry g, string on = "front") => Run(new SketchCreateOperation(Services), new SketchCreateRequest
    {
        On = on,
        Lines = g.Lines,
        CenterLines = g.CenterLines,
        Circles = g.Circles,
        Rectangles = g.Rectangles,
        Arcs = g.Arcs,
    });

    internal FeatureResult Extrude(FeatureExtrudeRequest request) => Run(new FeatureExtrudeOperation(Services), request);

    /// <summary>Sketch1 as a 10 mm blind boss: a block of 8000 mm³.</summary>
    internal FeatureResult Block() => Extrude(new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10 });

    internal FeatureResult Revolve(FeatureRevolveRequest request) => Run(new FeatureRevolveOperation(Services), request);

    /// <summary>A 10 x 30 rectangle from x = 10 to 20 beside a centerline on the y axis, the next sketch; turned about it, a tube.</summary>
    internal SketchCreateResult Tube() => Sketch(new SketchGeometry
    {
        Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "wall", Corner1 = P(10, 0), Corner2 = P(20, 30) } },
        CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = P(0, 0), End = P(0, 40) } },
    });

    internal FeatureResult Fillet(FeatureFilletRequest request) => Run(new FeatureFilletOperation(Services), request);

    internal FeatureResult Chamfer(FeatureChamferRequest request) => Run(new FeatureChamferOperation(Services), request);

    /// <summary>A straight edge of the part, as model.find would name it.</summary>
    internal string Edge(FakeDocument doc) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 10, Feature = "Boss-Extrude1" })).Reference!;

    /// <summary>A planar face of the part, as model.find would name it.</summary>
    internal string Face(FakeDocument doc) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, AreaMm2 = 800, Feature = "Boss-Extrude1" })).Reference!;

    /// <summary>A 5 mm radius circle at (20, 10), the next sketch.</summary>
    internal SketchCreateResult Hole() => Sketch(new SketchGeometry { Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = P(20, 10), RadiusMm = 5 } } });
}

public class FeatureExtrudeTests : FeatureTestBase
{
    [Fact]
    public void A_blind_boss_on_an_empty_part_adds_exactly_area_times_depth()
    {
        var doc = Part();
        var made = Block();
        Assert.Equal("Boss-Extrude1", made.Feature.Name);
        Assert.Equal(ReferenceKind.Feature, made.Feature.Kind);
        Assert.StartsWith("r1.feature.", made.Feature.Reference);
        Assert.Equal(6, made.Faces.Count);
        Assert.All(made.Faces, f => Assert.StartsWith("r1.face.", f.Reference));
        Assert.Equal("Boss-Extrude1", Assert.Single(made.Change.Created).Name);
        Assert.Equal(0, made.Change.VolumeBeforeMm3);
        Assert.Equal(8000, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("800 mm² × 10 mm"));
        var spec = doc.Extrudes.Single();
        Assert.False(spec.Cut);
        Assert.Equal(ExtrudeEnd.Blind, spec.End);
        Assert.Equal(10, spec.DepthMm);
        Assert.True(spec.Merge);
    }

    [Fact]
    public void A_cut_through_all_removes_material()
    {
        var doc = Part();
        Block();
        Hole();
        var cut = Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        Assert.Equal("Cut-Extrude1", cut.Feature.Name);
        Assert.Equal(8000 - Math.PI * 25 * 10, cut.Change.VolumeAfterMm3, 6);
        Assert.True(doc.Extrudes.Last().Cut);
        Assert.Contains(cut.Checks, c => c.StartsWith("the volume shrank"));
    }

    [Fact]
    public void Blind_both_ways_adds_exactly_area_times_both_depths()
    {
        var doc = Part();
        var made = Extrude(new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10, Direction2 = new ExtrudeDirection { DepthMm = 5 } });
        Assert.Equal(12000, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("800 mm² × 15 mm"));
        var second = doc.Extrudes.Single().Direction2!;
        Assert.Equal((ExtrudeEnd.Blind, 5.0), (second.End, second.DepthMm));
    }

    [Fact]
    public void A_second_direction_up_to_a_surface_resolves_its_own_face()
    {
        var doc = Part();
        var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
        Extrude(new FeatureExtrudeRequest
        {
            Sketch = "Sketch1",
            DepthMm = 10,
            Direction2 = new ExtrudeDirection { End = ExtrudeEnd.UpToSurface, UpTo = Services.References.Describe(doc, face).Reference },
        });
        Assert.Same(face, doc.Extrudes.Single().Direction2!.UpTo);
        Assert.Null(doc.Extrudes.Single().UpTo);
    }

    [Fact]
    public void A_thin_wall_is_checked_by_the_sign_of_its_volume_only()
    {
        var doc = Part();
        var made = Extrude(new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10, Thin = new ExtrudeThin { ThicknessMm = 1, Side = ThinSide.MidPlane } });
        Assert.Equal(400, made.Change.VolumeAfterMm3, 6);
        Assert.DoesNotContain(made.Checks, c => c.Contains("mm² ×"));
        var thin = doc.Extrudes.Single().Thin!;
        Assert.Equal((ThinSide.MidPlane, 1.0), (thin.Side, thin.ThicknessMm));
    }

    [Fact]
    public void Up_to_surface_ends_at_the_face_it_names()
    {
        var doc = Part();
        var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
        Extrude(new FeatureExtrudeRequest { Sketch = "Sketch1", End = ExtrudeEnd.UpToSurface, UpTo = Services.References.Describe(doc, face).Reference });
        Assert.Same(face, doc.Extrudes.Single().UpTo);
    }

    [Theory]
    [InlineData("no_depth", "depth_mm")]
    [InlineData("zero_depth", "depth_mm")]
    [InlineData("depth_through_all", "depth_mm")]
    [InlineData("no_up_to", "up_to")]
    [InlineData("up_to_blind", "up_to")]
    [InlineData("reverse_mid_plane", "reverse")]
    [InlineData("draft_90", "draft_deg")]
    [InlineData("merge_cut", "merge")]
    [InlineData("up_to_vertex_face", "up_to")]
    [InlineData("direction2_mid_plane", "direction2")]
    [InlineData("direction2_ends_mid_plane", "/direction2/end")]
    [InlineData("direction2_no_depth", "/direction2/depth_mm")]
    [InlineData("direction2_no_up_to", "/direction2/up_to")]
    [InlineData("direction2_draft_90", "/direction2/draft_deg")]
    [InlineData("thin_no_thickness", "/thin/thickness_mm")]
    [InlineData("thin_two_sides_one_thickness", "/thin/thickness2_mm")]
    [InlineData("thin_one_side_two_thicknesses", "/thin/thickness2_mm")]
    public void Every_wrong_field_is_named_and_nothing_is_made(string wrong, string field)
    {
        var doc = Part();
        var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
        var request = new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10 };
        switch (wrong)
        {
            case "no_depth": request.DepthMm = null; break;
            case "zero_depth": request.DepthMm = 0; break;
            case "depth_through_all": request.End = ExtrudeEnd.ThroughAll; break;
            case "no_up_to": request.End = ExtrudeEnd.UpToSurface; request.DepthMm = null; break;
            case "up_to_blind": request.UpTo = "front"; break;
            case "reverse_mid_plane": request.End = ExtrudeEnd.MidPlane; request.Reverse = true; break;
            case "draft_90": request.DraftDeg = 90; break;
            case "merge_cut": request.Mode = FeatureMode.Cut; request.Merge = true; break;
            case "up_to_vertex_face":
                request.End = ExtrudeEnd.UpToVertex;
                request.DepthMm = null;
                request.UpTo = Services.References.Describe(doc, face).Reference;
                break;
            case "direction2_mid_plane": request.End = ExtrudeEnd.MidPlane; request.Direction2 = new ExtrudeDirection { DepthMm = 5 }; break;
            case "direction2_ends_mid_plane": request.Direction2 = new ExtrudeDirection { End = ExtrudeEnd.MidPlane }; break;
            case "direction2_no_depth": request.Direction2 = new ExtrudeDirection(); break;
            case "direction2_no_up_to": request.Direction2 = new ExtrudeDirection { End = ExtrudeEnd.UpToVertex }; break;
            case "direction2_draft_90": request.Direction2 = new ExtrudeDirection { DepthMm = 5, DraftDeg = 90 }; break;
            case "thin_no_thickness": request.Thin = new ExtrudeThin(); break;
            case "thin_two_sides_one_thickness": request.Thin = new ExtrudeThin { ThicknessMm = 1, Side = ThinSide.TwoSides }; break;
            case "thin_one_side_two_thicknesses": request.Thin = new ExtrudeThin { ThicknessMm = 1, Thickness2Mm = 2 }; break;
        }
        var e = Fails(() => Extrude(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(doc.Extrudes);
    }

    [Fact]
    public void A_sketch_of_construction_lines_only_is_refused()
    {
        var doc = Part();
        Sketch(new SketchGeometry { CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = P(0, 0), End = P(0, 10) } } });
        var e = Fails(() => Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", DepthMm = 5 }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("sketch", e.Error.Details!["field"]);
        Assert.Empty(doc.Extrudes);
    }

    [Fact]
    public void A_boss_that_adds_the_wrong_volume_is_deleted_and_its_sketch_kept()
    {
        var doc = Part();
        doc.FeatureVolumeError = 5;
        var e = Fails(() => Block());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.Equal(new List<object?> { "Boss-Extrude1" }, e.Error.Details["rolled_back"]);
        Assert.Equal(("Boss-Extrude1", false, false), doc.Deletes.Single());
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Boss-Extrude1");
        Assert.Contains(doc.Tree, f => f.Name == "Sketch1");
        Assert.Equal(0, doc.MassProperties!.VolumeMm3, 9);
        Assert.Equal(new[] { false }, doc.Rebuilds);
    }

    [Fact]
    public void A_cut_that_removes_nothing_is_rolled_back()
    {
        var doc = Part();
        Block();
        Hole();
        doc.FeatureAddsNothing = true;
        var e = Fails(() => Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll }));
        Assert.Equal("volume_change", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Cut-Extrude1");
    }

    [Fact]
    public void A_feature_of_another_type_is_rolled_back()
    {
        var doc = Part();
        doc.FeatureTypeOverride = "Cut";
        Assert.Equal("feature_type", Fails(() => Block()).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Cut");
    }

    [Fact]
    public void A_feature_SOLIDWORKS_does_not_make_is_operation_failed_with_its_errors()
    {
        var doc = Part();
        doc.FeatureRefuses = true;
        doc.Problems.Add(new RebuildItem { Feature = "Sketch1", Code = "swSketchErrorOpenContour" });
        var e = Fails(() => Block());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("swSketchErrorOpenContour", e.Message);
    }

    [Fact]
    public void On_a_part_with_a_body_a_boss_is_checked_by_the_sign_of_its_volume_only()
    {
        var doc = Part();
        doc.Bodies = 1;
        doc.MassProperties!.VolumeMm3 = 1000;
        doc.FeatureVolumeError = 5;
        var made = Block();
        Assert.Equal(9005, made.Change.VolumeAfterMm3, 6);
        Assert.DoesNotContain(made.Checks, c => c.Contains("mm² ×"));
    }

    [Fact]
    public void Nothing_starts_while_the_user_edits_a_sketch()
    {
        var doc = Part();
        doc.EditingSketchName = "Sketch1";
        Assert.Equal(ErrorCode.Busy, Fails(() => Block()).Code);
        Assert.Empty(doc.Extrudes);
    }
}

public class FeatureRevolveTests : FeatureTestBase
{
    string Axis(SketchCreateResult sketch) => sketch.Ids.Single(i => i.Id == "axis").References[0];

    [Fact]
    public void A_full_turn_about_the_sketchs_centerline_is_exactly_Pappus_volume()
    {
        var doc = Part();
        var tube = Tube();
        var made = Revolve(new FeatureRevolveRequest { Sketch = "Sketch2", Axis = Axis(tube) });
        Assert.Equal("Revolve1", made.Feature.Name);
        Assert.Equal(Math.PI * (400 - 100) * 30, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("Pappus") && c.Contains("360°"));
        var spec = doc.Revolves.Single();
        Assert.Equal(360, spec.AngleDeg);
        Assert.False(spec.Cut);
        Assert.Equal(Axis(tube), Services.References.Describe(doc, spec.Axis).Reference);
    }

    [Fact]
    public void A_quarter_turn_is_a_quarter_of_the_volume()
    {
        Part();
        var tube = Tube();
        var made = Revolve(new FeatureRevolveRequest { Sketch = tube.Sketch.Name, Axis = Axis(tube), AngleDeg = 90 });
        Assert.Equal(Math.PI * (400 - 100) * 30 / 4, made.Change.VolumeAfterMm3, 6);
    }

    [Fact]
    public void An_axis_in_another_sketch_or_not_straight_is_refused()
    {
        var doc = Part();
        var first = Tube();
        Tube();
        var e = Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch3", Axis = Axis(first) }));
        Assert.Equal("axis", e.Error.Details!["field"]);
        Assert.Contains("Sketch2", e.Message);
        var round = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle });
        e = Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch3", Axis = Services.References.Describe(doc, round).Reference! }));
        Assert.Equal("axis", e.Error.Details!["field"]);
        Assert.Contains("circle", e.Message);
        Assert.Equal("axis", Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch3", Axis = "front" })).Error.Details!["field"]);
        Assert.Empty(doc.Revolves);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(361)]
    public void An_angle_outside_0_to_360_is_refused(double angle)
    {
        var doc = Part();
        var tube = Tube();
        Assert.Equal("angle_deg", Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch2", Axis = Axis(tube), AngleDeg = angle })).Error.Details!["field"]);
        Assert.Empty(doc.Revolves);
    }

    [Fact]
    public void A_revolve_off_Pappus_volume_is_deleted_and_its_sketch_kept()
    {
        var doc = Part();
        var tube = Tube();
        doc.FeatureVolumeError = -3;
        var e = Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch2", Axis = Axis(tube) }));
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Revolve1");
        Assert.Contains(doc.Tree, f => f.Name == "Sketch2");
    }

    [Fact]
    public void A_cut_revolve_removes_material_and_is_a_RevCut()
    {
        var doc = Part();
        Block();
        var tube = Tube();
        var cut = Revolve(new FeatureRevolveRequest { Sketch = "Sketch2", Axis = Axis(tube), Mode = FeatureMode.Cut });
        Assert.Equal("Cut-Revolve1", cut.Feature.Name);
        Assert.True(cut.Change.VolumeAfterMm3 < cut.Change.VolumeBeforeMm3);
        Assert.True(doc.Revolves.Single().Cut);
        Assert.Equal("merge", Fails(() => Revolve(new FeatureRevolveRequest { Sketch = "Sketch2", Axis = Axis(tube), Mode = FeatureMode.Cut, Merge = false })).Error.Details!["field"]);
    }
}

public class FeatureFilletTests : FeatureTestBase
{
    [Fact]
    public void Rounds_the_edges_named_and_checks_they_are_gone()
    {
        var doc = Part();
        Block();
        var edges = new List<string> { Edge(doc), Edge(doc) };
        var made = Fillet(new FeatureFilletRequest { Edges = edges, RadiusMm = 2 });
        Assert.Equal("Fillet1", made.Feature.Name);
        Assert.Equal(2, made.Faces.Count);
        Assert.Contains("the 2 edges named are gone, and the fillet made 2 faces", made.Checks);
        Assert.Equal(8000 - 2 * 8.584, made.Change.VolumeAfterMm3, 6);
        Assert.Equal(2, doc.Fillets.Single().RadiusMm);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Services.References.Resolve(doc, edges[0], "edge", ReferenceKind.Edge)).Code);
    }

    [Fact]
    public void Faces_alone_round_their_edges()
    {
        var doc = Part();
        Block();
        Fillet(new FeatureFilletRequest { Faces = new List<string> { Face(doc) }, RadiusMm = 1 });
        Assert.Equal(ReferenceKind.Face, doc.KindOf(doc.Fillets.Single().Targets.Single()));
    }

    [Theory]
    [InlineData("nothing", "edges")]
    [InlineData("no_radius", "radius_mm")]
    [InlineData("twice", "/edges/1")]
    [InlineData("face_as_edge", "/edges/0")]
    public void A_request_that_names_nothing_or_names_it_wrongly_is_refused(string wrong, string field)
    {
        var doc = Part();
        Block();
        string edge = Edge(doc);
        var request = new FeatureFilletRequest { Edges = new List<string> { edge }, RadiusMm = 2 };
        switch (wrong)
        {
            case "nothing": request.Edges = null; break;
            case "no_radius": request.RadiusMm = null; break;
            case "twice": request.Edges.Add(edge); break;
            case "face_as_edge": request.Edges = new List<string> { Face(doc) }; break;
        }
        Assert.Equal(field, Fails(() => Fillet(request)).Error.Details!["field"]);
        Assert.Empty(doc.Fillets);
    }

    [Fact]
    public void An_edge_still_there_afterwards_rolls_the_fillet_back()
    {
        var doc = Part();
        Block();
        doc.EdgesSurvive = true;
        var e = Fails(() => Fillet(new FeatureFilletRequest { Edges = new List<string> { Edge(doc) }, RadiusMm = 2 }));
        Assert.Equal("edges_gone", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Fillet1");
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3, 6);
    }

    [Fact]
    public void A_fillet_that_leaves_the_volume_as_it_was_is_rolled_back()
    {
        var doc = Part();
        Block();
        doc.FeatureAddsNothing = true;
        Assert.Equal("volume_change", Fails(() => Fillet(new FeatureFilletRequest { Edges = new List<string> { Edge(doc) }, RadiusMm = 2 })).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Fillet1");
    }
}
public class FeatureChamferTests : FeatureTestBase
{
    [Fact]
    public void Bevels_at_45_degrees_unless_told_otherwise()
    {
        var doc = Part();
        Block();
        var made = Chamfer(new FeatureChamferRequest { Edges = new List<string> { Edge(doc) }, DistanceMm = 2 });
        Assert.Equal("Chamfer1", made.Feature.Name);
        Assert.Contains("the 1 edges named are gone, and the chamfer made 1 faces", made.Checks);
        var spec = doc.Chamfers.Single().Spec;
        Assert.Equal(2, spec.DistanceMm);
        Assert.Equal(45, spec.AngleDeg);
        Assert.Null(spec.Distance2Mm);
    }

    [Fact]
    public void Two_distances_make_a_distance_distance_chamfer()
    {
        var doc = Part();
        Block();
        Chamfer(new FeatureChamferRequest { Edges = new List<string> { Edge(doc) }, DistanceMm = 2, Distance2Mm = 3 });
        Assert.Equal(3, doc.Chamfers.Single().Spec.Distance2Mm);
    }

    [Theory]
    [InlineData("no_edges", "edges")]
    [InlineData("no_distance", "distance_mm")]
    [InlineData("both", "distance2_mm")]
    [InlineData("angle_90", "angle_deg")]
    public void A_wrong_request_is_refused_by_its_field(string wrong, string field)
    {
        var doc = Part();
        Block();
        var request = new FeatureChamferRequest { Edges = new List<string> { Edge(doc) }, DistanceMm = 2 };
        switch (wrong)
        {
            case "no_edges": request.Edges.Clear(); break;
            case "no_distance": request.DistanceMm = null; break;
            case "both": request.AngleDeg = 30; request.Distance2Mm = 2; break;
            case "angle_90": request.AngleDeg = 90; break;
        }
        Assert.Equal(field, Fails(() => Chamfer(request)).Error.Details!["field"]);
        Assert.Empty(doc.Chamfers);
    }

    [Fact]
    public void An_edge_still_there_afterwards_rolls_the_chamfer_back()
    {
        var doc = Part();
        Block();
        doc.EdgesSurvive = true;
        Assert.Equal("edges_gone", Fails(() => Chamfer(new FeatureChamferRequest { Edges = new List<string> { Edge(doc) }, DistanceMm = 2 })).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Chamfer1");
    }
}
public class FeatureSuppressTests : FeatureTestBase
{
    FeatureSuppressResult Suppress(FeatureSuppressRequest request) => Run(new FeatureSuppressOperation(Services), request);

    [Fact]
    public void Suppresses_and_unsuppresses_by_name_and_reports_the_change()
    {
        var doc = Part();
        Block();
        Hole();
        var cut = Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        var off = Suppress(new FeatureSuppressRequest { Features = new List<string> { cut.Feature.Reference! } });
        Assert.Equal("Cut-Extrude1", off.Features.Single().Name);
        var changed = off.Change.Changed.Single();
        Assert.Equal(("suppressed", "unsuppressed", "suppressed"), (changed.Property, changed.Before, changed.After));
        Assert.Equal(8000, off.Change.VolumeAfterMm3, 6);
        Assert.True(doc.Tree.Single(f => f.Name == "Cut-Extrude1").Suppressed);
        var on = Suppress(new FeatureSuppressRequest { Features = new List<string> { "Cut-Extrude1" }, Suppressed = false });
        Assert.Equal(8000 - Math.PI * 250, on.Change.VolumeAfterMm3, 6);
        Assert.Equal(new[] { false, false }, doc.Rebuilds);
    }

    [Fact]
    public void A_feature_already_as_asked_is_left_alone()
    {
        var doc = Part();
        Block();
        var result = Suppress(new FeatureSuppressRequest { Features = new List<string> { "Boss-Extrude1" }, Suppressed = false });
        Assert.Equal(new[] { "Boss-Extrude1" }, result.Unchanged);
        Assert.Empty(result.Change.Changed);
        Assert.Empty(doc.Suppressions);
    }

    [Theory]
    [InlineData("none", "features")]
    [InlineData("twice", "/features/1")]
    [InlineData("a_face", "/features/0")]
    public void A_request_naming_nothing_or_naming_it_wrongly_is_refused(string wrong, string field)
    {
        var doc = Part();
        Block();
        var request = new FeatureSuppressRequest { Features = new List<string> { "Boss-Extrude1" } };
        switch (wrong)
        {
            case "none": request.Features.Clear(); break;
            case "twice": request.Features.Add("Boss-Extrude1"); break;
            case "a_face": request.Features = new List<string> { Face(doc) }; break;
        }
        Assert.Equal(field, Fails(() => Suppress(request)).Error.Details!["field"]);
        Assert.Empty(doc.Suppressions);
    }

    [Fact]
    public void A_feature_that_does_not_take_the_suppression_is_put_back()
    {
        var doc = Part();
        Block();
        doc.SuppressIgnored = true;
        var e = Fails(() => Suppress(new FeatureSuppressRequest { Features = new List<string> { "Boss-Extrude1" } }));
        Assert.Equal("suppressed", e.Error.Details!["check"]);
        Assert.Equal(new[] { ("Boss-Extrude1", true), ("Boss-Extrude1", false) }, doc.Suppressions);
    }

    [Fact]
    public void SOLIDWORKS_refusing_is_operation_failed_and_nothing_stays_changed()
    {
        var doc = Part();
        Block();
        doc.SuppressRefused = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Suppress(new FeatureSuppressRequest { Features = new List<string> { "Boss-Extrude1" } })).Code);
        Assert.False(doc.Tree.Single(f => f.Name == "Boss-Extrude1").Suppressed);
    }
}

public class FeatureDeleteTests : FeatureTestBase
{
    FeatureDeleteResult Delete(FeatureDeleteRequest request) => Run(new FeatureDeleteOperation(Services), request);

    [Fact]
    public void Deletes_a_feature_with_its_sketch_and_reports_it()
    {
        var doc = Part();
        Block();
        Hole();
        Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        var result = Delete(new FeatureDeleteRequest { Features = new List<string> { "Cut-Extrude1" } });
        Assert.Equal(new[] { "Cut-Extrude1" }, result.Deleted);
        Assert.Equal(("Cut-Extrude1", false, true), doc.Deletes.Last());
        Assert.Equal(8000, result.Change.VolumeAfterMm3, 6);
        Assert.Equal("deleted", result.Change.Changed.Single().Property);
    }

    [Fact]
    public void A_feature_with_children_is_refused_unless_they_go_too()
    {
        var doc = Part();
        Block();
        doc.AddFeature("Fillet1", "Fillet");
        doc.Children["Boss-Extrude1"] = new List<string> { "Fillet1" };
        var e = Fails(() => Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" } }));
        Assert.Equal("children", e.Error.Details!["field"]);
        Assert.Equal(new List<object?> { "Fillet1" }, e.Error.Details["children"]);
        Assert.Empty(doc.Deletes);
        var result = Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" }, Children = DeleteChildren.Delete, KeepAbsorbed = true });
        Assert.Equal(new[] { "Boss-Extrude1", "Fillet1" }, result.Deleted);
        Assert.Equal(("Boss-Extrude1", true, false), doc.Deletes.First());
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Fillet1");
    }

    [Fact]
    public void Grandchildren_are_refused_reported_and_checked_like_children()
    {
        var doc = Part();
        Block();
        doc.AddFeature("Fillet1", "Fillet");
        doc.AddFeature("Chamfer1", "Chamfer");
        doc.Children["Boss-Extrude1"] = new List<string> { "Fillet1" };
        doc.Children["Fillet1"] = new List<string> { "Chamfer1" };
        var e = Fails(() => Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" } }));
        Assert.Equal(new List<object?> { "Fillet1", "Chamfer1" }, e.Error.Details!["children"]);
        var result = Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" }, Children = DeleteChildren.Delete });
        Assert.Equal(new[] { "Boss-Extrude1", "Fillet1", "Chamfer1" }, result.Deleted);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Chamfer1");
    }

    [Fact]
    public void A_grandchild_left_behind_is_rollback_failed_naming_it()
    {
        var doc = Part();
        Block();
        doc.AddFeature("Fillet1", "Fillet");
        doc.AddFeature("Chamfer1", "Chamfer");
        doc.Children["Boss-Extrude1"] = new List<string> { "Fillet1" };
        doc.Children["Fillet1"] = new List<string> { "Chamfer1" };
        doc.DeleteSpares.Add("Chamfer1");
        var e = Fails(() => Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" }, Children = DeleteChildren.Delete }));
        Assert.True((bool)e.Error.Details!["rollback_failed"]!);
        Assert.Contains("Chamfer1", e.Message);
    }

    [Fact]
    public void A_feature_named_after_one_it_is_built_on_is_reported_once()
    {
        var doc = Part();
        Block();
        doc.AddFeature("Fillet1", "Fillet");
        doc.Children["Boss-Extrude1"] = new List<string> { "Fillet1" };
        var result = Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1", "Fillet1" }, Children = DeleteChildren.Delete });
        Assert.Equal(new[] { "Boss-Extrude1", "Fillet1" }, result.Deleted);
    }

    [Fact]
    public void Children_that_are_named_too_are_not_refused()
    {
        var doc = Part();
        Block();
        doc.AddFeature("Fillet1", "Fillet");
        doc.Children["Boss-Extrude1"] = new List<string> { "Fillet1" };
        var result = Delete(new FeatureDeleteRequest { Features = new List<string> { "Fillet1", "Boss-Extrude1" } });
        Assert.Equal(new[] { "Fillet1", "Boss-Extrude1" }, result.Deleted);
    }

    [Fact]
    public void A_feature_still_there_afterwards_is_rollback_failed_naming_it()
    {
        var doc = Part();
        Block();
        doc.DeleteIgnored = true;
        var e = Fails(() => Delete(new FeatureDeleteRequest { Features = new List<string> { "Boss-Extrude1" } }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.True((bool)e.Error.Details!["rollback_failed"]!);
        Assert.Contains("Boss-Extrude1", e.Message);
    }
}
