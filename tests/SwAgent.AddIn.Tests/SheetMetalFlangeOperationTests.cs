using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Flanges and hems on the 40 × 20 × 2 tab (Sketch1 at R 3): its base face at z = 0 facing +z, the sheet below it, and
/// its four edges where it ends, each with the thin face it ends in.
/// </summary>
public abstract class SheetMetalFlangeTestBase : SheetMetalTestBase
{
    /// <summary>A 90° bend's folded material per mm of width at R 3, T 2: π/4 × (25 − 9).</summary>
    internal static readonly double Bend90 = Math.PI / 4 * 16;

    internal FakeDocument Sheet()
    {
        var doc = Part();
        Tab();
        doc.ExactBox = new Box3 { Min = new Point3(0, 0, -2), Max = new Point3(40, 20, 0) };
        return doc;
    }

    internal string Ref(FakeDocument doc, object entity) => Services.References.Describe(doc, entity).Reference!;

    /// <summary>The 40 mm edge along y = 20.</summary>
    internal string Top(FakeDocument doc) => Ref(doc, doc.SheetEdge(new Point3(0, 20, 0), new Point3(40, 20, 0), new Vector3(0, 1, 0)));

    /// <summary>The 40 mm edge along y = 0.</summary>
    internal string Bottom(FakeDocument doc) => Ref(doc, doc.SheetEdge(new Point3(0, 0, 0), new Point3(40, 0, 0), new Vector3(0, -1, 0)));

    /// <summary>The 20 mm edge along x = 40.</summary>
    internal string Right(FakeDocument doc) => Ref(doc, doc.SheetEdge(new Point3(40, 0, 0), new Point3(40, 20, 0), new Vector3(1, 0, 0)));

    internal SheetMetalFeatureResult EdgeFlange(SheetMetalEdgeFlangeRequest request) => Run(new SheetMetalEdgeFlangeOperation(Services), request);
}

public class SheetMetalEdgeFlangeTests : SheetMetalFlangeTestBase
{
    /// <summary>A 90° material-inside flange 25 mm from the inner sharp at R 3, T 2, per mm: 22 straight less 5 taken back, and the bend.</summary>
    static readonly double Per25 = 2 * (22 - 5) + Bend90;

    [Fact]
    public void A_flange_rises_from_its_edge_and_adds_exactly_its_straight_part_and_bend_less_the_sheet_taken_back()
    {
        var doc = Sheet();
        var made = EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        Assert.Equal("Edge-Flange1", made.Feature.Name);
        Assert.Null(made.Features);
        Assert.Equal(1600 + 40 * Per25, made.Change.VolumeAfterMm3, 6);
        var bend = Assert.Single(made.Sheet.Bends);
        Assert.Equal("Edge-Flange1", bend.Feature);
        Assert.Equal(90, bend.AngleDeg);
        Assert.Equal(3, bend.InnerRadiusMm);
        Assert.Equal(0.5, bend.Allowance.KFactor);
        Assert.Equal(25, doc.ExactBox!.Max.Z, 9);
        Assert.Contains(made.Checks, c => c.Contains("1 bends, one per flange, at 90°, inner radius 3 mm and K 0.5"));
        Assert.Contains(made.Checks, c => c.Contains("the flange geometry's 40 mm of flange") && c.Contains("22 mm straight part"));
        Assert.Contains(made.Checks, c => c.Contains("Edge-Flange1 rises 25 mm above its face"));
        Assert.Equal(new[] { "Sketch2", "Edge-Flange1" }, made.Change.Created.Select(c => c.Name));
        var (_, sketch, spec) = doc.EdgeFlanges.Single();
        Assert.Null(spec.RadiusMm);
        Assert.Null(spec.Allowance);
        Assert.Equal(BendPosition.MaterialInside, spec.Position);
        Assert.Equal(FlangeLengthFrom.InnerVirtualSharp, spec.LengthFrom);
        var profile = doc.ReadSketch(sketch).Segments;
        Assert.Equal(4, profile.Count);
        Assert.Equal(25, profile.Max(s => Math.Max(s.Start!.Y, s.End!.Y)), 9);
        Assert.Equal(40, profile.Max(s => Math.Max(s.Start!.X, s.End!.X)), 9);
        Assert.True(doc.Editors.Last().Closed);
    }

    [Fact]
    public void Measured_from_the_outer_sharp_by_default_27_mm_is_the_same_flange()
    {
        var doc = Sheet();
        var made = EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 27 });
        Assert.Equal(1600 + 40 * Per25, made.Change.VolumeAfterMm3, 6);
        Assert.Equal(FlangeLengthFrom.OuterVirtualSharp, doc.EdgeFlanges.Single().Spec.LengthFrom);
    }

    [Fact]
    public void Several_edges_make_one_flange_each_and_the_result_lists_them_all()
    {
        var doc = Sheet();
        var made = EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc), Bottom(doc) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        Assert.Equal(new[] { "Edge-Flange1", "Edge-Flange2" }, made.Features!.Select(f => f.Name));
        Assert.Equal(2, made.Sheet.Bends.Count);
        Assert.Equal(1600 + 80 * Per25, made.Change.VolumeAfterMm3, 6);
        Assert.Equal(6, made.Faces.Count);
        Assert.Contains(made.Checks, c => c.Contains("2 bends, one per flange"));
    }

    [Fact]
    public void Its_own_radius_and_allowance_reach_its_bend()
    {
        var doc = Sheet();
        var made = EdgeFlange(new SheetMetalEdgeFlangeRequest
        {
            Edges = new List<string> { Top(doc) },
            LengthMm = 25,
            LengthFrom = FlangeLengthFrom.InnerVirtualSharp,
            BendRadiusMm = 5,
            Allowance = new BendAllowance { KFactor = 0.1 },
        });
        var bend = Assert.Single(made.Sheet.Bends);
        Assert.Equal(5, bend.InnerRadiusMm);
        Assert.Equal(0.1, bend.Allowance.KFactor);
        Assert.False(bend.DefaultRadius);
        Assert.Equal(1600 + 40 * (2 * (20 - 7) + Math.PI / 4 * (49 - 25)), made.Change.VolumeAfterMm3, 6);
        Assert.Equal(5, doc.EdgeFlanges.Single().Spec.RadiusMm);
    }

    [Fact]
    public void A_flange_short_of_its_edge_is_checked_within_its_reliefs()
    {
        var doc = Sheet();
        var made = EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp, OffsetMm = 5 });
        Assert.Equal(1600 + 30 * Per25, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("less at most") && c.Contains("reliefs"));
        var lines = doc.ReadSketch(doc.EdgeFlanges.Single().Sketch).Segments;
        Assert.Equal(5, lines.Min(s => Math.Min(s.Start!.X, s.End!.X)), 9);
        Assert.Equal(35, lines.Max(s => Math.Max(s.Start!.X, s.End!.X)), 9);
    }

    [Fact]
    public void Requests_that_cannot_make_an_edge_flange_name_their_field()
    {
        var doc = Sheet();
        string top = Top(doc);
        string Field(SheetMetalEdgeFlangeRequest r) => (string)Fails(() => EdgeFlange(r)).Error.Details!["field"]!;
        Assert.Equal("edges", Field(new SheetMetalEdgeFlangeRequest { LengthMm = 25 }));
        Assert.Equal("length_mm", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top } }));
        Assert.Equal("length_mm", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 2, LengthFrom = FlangeLengthFrom.InnerVirtualSharp }));
        Assert.Equal("angle_deg", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25, AngleDeg = 180 }));
        Assert.Equal("offset_mm", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25, OffsetMm = 20 }));
        Assert.Equal("allowance", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25, Allowance = new BendAllowance() }));
        Assert.Equal("bend_radius_mm", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25, BendRadiusMm = -1 }));
        Assert.Equal("/edges/1", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top, top }, LengthMm = 25 }));
        string inside = Ref(doc, doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 40, Origin = new Point3(20, 10, 0), Axis = new Vector3(1, 0, 0) }));
        Assert.Equal("/edges/0", Field(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { inside }, LengthMm = 25 }));
        Assert.Empty(doc.EdgeFlanges);
    }

    [Fact]
    public void An_ordinary_part_is_the_wrong_document_type()
    {
        var doc = Part();
        Block();
        var e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Edge(doc) }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("sheet_metal", e.Error.Details!["needs"]);
    }

    [Fact]
    public void A_flattened_part_is_refused_and_nothing_folds_it()
    {
        var sheet = Sheet();
        sheet.Suppress(sheet.FeatureByName("Flat-Pattern1")!, false);
        var e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(sheet) }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("flattened", e.Error.Details!["state"]);
        Assert.Empty(sheet.EdgeFlanges);
    }

    [Fact]
    public void A_flange_SOLIDWORKS_does_not_make_takes_its_sketch_back()
    {
        var doc = Sheet();
        string top = Top(doc);
        doc.FeatureRefuses = true;
        var e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("SOLIDWORKS made no edge flange on /edges/0", e.Message);
        Assert.Null(doc.FeatureByName("Sketch2"));
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);

        doc.FeatureRefuses = false;
        doc.EdgeSketchRefuses = true;
        e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("sketch for an edge flange", e.Message);
    }

    [Fact]
    public void A_volume_off_the_flange_geometry_rolls_every_flange_and_sketch_back()
    {
        var doc = Sheet();
        string top = Top(doc), bottom = Bottom(doc);
        doc.FeatureVolumeError = 3;
        var e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top, bottom }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "Edge-Flange2", "Sketch3", "Edge-Flange1", "Sketch2" }, ((IEnumerable<object?>)e.Error.Details["rolled_back"]!).ToArray());
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "EdgeFlange" || f.Type == "OneBend");
        Assert.Equal(0, doc.ExactBox!.Max.Z, 9);
    }

    [Theory]
    [InlineData("bend_angle")]
    [InlineData("height")]
    [InlineData("feature_type")]
    public void A_flange_that_is_not_what_was_asked_is_rolled_back(string check)
    {
        var doc = Sheet();
        string top = Top(doc);
        switch (check)
        {
            case "bend_angle":
                doc.BendAngleError = 1;
                break;
            case "height":
                doc.FlangeRiseError = 0.5;
                break;
            case "feature_type":
                doc.FeatureTypeOverride = "Extrusion";
                break;
        }
        var e = Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { top }, LengthMm = 25 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
        Assert.Null(doc.FeatureByName("Edge-Flange1"));
    }
}
