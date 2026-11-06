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
