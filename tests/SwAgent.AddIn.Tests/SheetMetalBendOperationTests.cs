using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// 6.C's operations on the 40 × 20 × 2 tab at R 3: its base face at z = 0 facing +z, its centre (20, 10, 0) on it, the
/// sheet below it.
/// </summary>
public abstract class SheetMetalBendTestBase : SheetMetalFlangeTestBase
{
    internal FakeDocument Tabbed()
    {
        var doc = Sheet();
        var face = doc.SheetFaceList[0];
        face.Info.Origin = new Point3(20, 10, 0);
        face.Info.DistanceMm = 0;
        doc.SheetFaceList[1].Info.Origin = new Point3(20, 10, 2);
        return doc;
    }

    /// <summary>The tab's base face, by reference.</summary>
    internal string Base(FakeDocument doc) => Ref(doc, doc.SheetFaceList[0]);

    /// <summary>Lines across the tab on its base face at these x, from y = -5 to 25: the next sketch.</summary>
    internal SketchCreateResult Lines(FakeDocument doc, params double[] xs) => Sketch(new SketchGeometry
    {
        Lines = xs.Select((x, i) => new SketchLine { Id = $"bend{i + 1}", Start = P(x, -5), End = P(x, 25) }).ToList(),
    }, Base(doc));

    internal SheetMetalFeatureResult SketchedBend(SheetMetalSketchedBendRequest request) => Run(new SheetMetalSketchedBendOperation(Services), request);
}

public class SheetMetalSketchedBendTests : SheetMetalBendTestBase
{
    [Fact]
    public void A_line_bends_the_sheet_once_and_at_half_the_volume_stays()
    {
        var doc = Tabbed();
        var sketch = Lines(doc, 30);
        var made = SketchedBend(new SheetMetalSketchedBendRequest { Sketch = sketch.Sketch.Name, FixedFace = Base(doc), AngleDeg = 90 });
        Assert.Equal("Sketched Bend1", made.Feature.Name);
        var bend = Assert.Single(made.Sheet.Bends);
        Assert.Equal("SketchBend1", bend.Name);
        Assert.Equal(90, bend.AngleDeg);
        Assert.Equal(3, bend.InnerRadiusMm);
        Assert.Equal(0.5, bend.Allowance.KFactor);
        Assert.Equal(1600, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c == "1 bends, one per line, at 90°, inner radius 3 mm and K 0.5");
        Assert.Contains(made.Checks, c => c.StartsWith("the volume changed by 0 mm³, the bend arithmetic's 0 mm³ (folding 1 bends"));
        Assert.Contains(made.Checks, c => c.StartsWith("the fixed face stayed put: a face of the sheet still lies through (20, 10, 0)"));
        Assert.Contains(made.Checks, c => c.Contains("measures 2 mm between the sheet's two faces"));
        Assert.Equal(new[] { "Sketched Bend1" }, made.Change.Created.Select(c => c.Name));
        var (_, point, spec) = doc.SketchedBends.Single();
        Assert.Equal((20.0, 10.0, 0.0), (point.X, point.Y, point.Z));
        Assert.Equal(BendPosition.BendOutside, spec.Position);
        Assert.Null(spec.RadiusMm);
        Assert.Null(spec.Allowance);
        Assert.False(spec.Flip);
    }

    [Fact]
    public void Two_lines_make_a_channel_and_their_own_K_factor_folds_more_material_than_it_develops()
    {
        var doc = Tabbed();
        var sketch = Lines(doc, 10, 30);
        var made = SketchedBend(new SheetMetalSketchedBendRequest
        {
            Sketch = sketch.Sketch.Name,
            FixedFace = Base(doc),
            AngleDeg = 90,
            BendRadiusMm = 3,
            Position = BendPosition.MaterialInside,
            Flip = true,
            Allowance = new BendAllowance { KFactor = 0.1 },
        });
        Assert.Equal(new[] { "SketchBend1", "SketchBend2" }, made.Sheet.Bends.Select(b => b.Name));
        // Each 30 mm line folds 30 × 2 × π/2 × (4 − 3.2) more than it develops to at K 0.1.
        Assert.Equal(1600 + 2 * 30 * 2 * Math.PI / 2 * 0.8, made.Change.VolumeAfterMm3, 6);
        Assert.All(made.Sheet.Bends, b => Assert.Equal(0.1, b.Allowance.KFactor));
        var spec = doc.SketchedBends.Single().Spec;
        Assert.Equal(BendPosition.MaterialInside, spec.Position);
        Assert.True(spec.Flip);
        Assert.Equal(3, spec.RadiusMm);
    }

    [Fact]
    public void What_it_is_given_is_checked_before_SOLIDWORKS_sees_it()
    {
        var doc = Tabbed();
        string lines = Lines(doc, 30).Sketch.Name;
        string circle = Sketch(new SketchGeometry { Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = P(5, 5), RadiusMm = 1 } } }, Base(doc)).Sketch.Name;
        string onTop = Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "l", Start = P(0, 0), End = P(10, 0) } } }, "top").Sketch.Name;
        string throughCentre = Lines(doc, 20).Sketch.Name;
        string Field(SheetMetalSketchedBendRequest r) => (string)Fails(() => SketchedBend(r)).Error.Details!["field"]!;
        Assert.Equal("angle_deg", Field(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc) }));
        Assert.Equal("angle_deg", Field(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc), AngleDeg = 180 }));
        Assert.Equal("bend_radius_mm", Field(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc), AngleDeg = 90, BendRadiusMm = 0 }));
        Assert.Equal("allowance", Field(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc), AngleDeg = 90, Allowance = new BendAllowance() }));
        Assert.Equal("sketch", Field(new SheetMetalSketchedBendRequest { Sketch = circle, FixedFace = Base(doc), AngleDeg = 90 }));
        Assert.Equal("sketch", Field(new SheetMetalSketchedBendRequest { Sketch = onTop, FixedFace = Base(doc), AngleDeg = 90 }));
        var e = Fails(() => SketchedBend(new SheetMetalSketchedBendRequest { Sketch = throughCentre, FixedFace = Base(doc), AngleDeg = 90 }));
        Assert.Equal("fixed_face", e.Error.Details!["field"]);
        Assert.Contains("lies on the line", e.Message);
        Assert.Empty(doc.SketchedBends);
    }

    [Fact]
    public void A_flattened_part_is_refused()
    {
        var doc = Tabbed();
        string lines = Lines(doc, 30).Sketch.Name;
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        var e = Fails(() => SketchedBend(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc), AngleDeg = 90 }));
        Assert.Equal("flattened", e.Error.Details!["state"]);
        Assert.Empty(doc.SketchedBends);
    }

    [Fact]
    public void A_part_that_is_not_sheet_metal_is_the_wrong_document_type()
    {
        Part();
        Block();
        var e = Fails(() => SketchedBend(new SheetMetalSketchedBendRequest { Sketch = "Sketch1", FixedFace = "r1.face.x", AngleDeg = 90 }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("sheet_metal", e.Error.Details!["needs"]);
    }

    [Theory]
    [InlineData("angle")]
    [InlineData("volume")]
    [InlineData("fixed")]
    public void A_bend_that_is_not_what_was_asked_is_rolled_back(string fault)
    {
        var doc = Tabbed();
        string lines = Lines(doc, 30).Sketch.Name;
        if (fault == "angle") doc.BendAngleError = 1;
        if (fault == "volume") doc.BendVolumeError = 5;
        if (fault == "fixed") doc.FixedFaceMoves = 2;
        var e = Fails(() => SketchedBend(new SheetMetalSketchedBendRequest { Sketch = lines, FixedFace = Base(doc), AngleDeg = 90 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(fault switch { "angle" => "bend_angle", "volume" => "volume_developed", _ => "fixed_face" }, e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "SM3dBend");
        Assert.Empty(doc.SheetMetal.Bends());
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
        Assert.Equal(0, doc.SheetFaceList[0].Info.DistanceMm);
        Assert.NotNull(doc.FeatureByName(lines));
    }
}

public class SheetMetalFlattenTests : SheetMetalBendTestBase
{
    internal SheetMetalFlattenResult Flatten(bool flat) => Run(new SheetMetalFlattenOperation(Services), new SheetMetalFlattenRequest { Flat = flat });

    /// <summary>The tab with a 25 mm flange on its 40 mm edge (at the allowance given), which flattens to 40 × 20 + the flange's development, 2 mm thin.</summary>
    FakeDocument Flanged(BendAllowance? allowance = null)
    {
        var doc = Tabbed();
        EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp, Allowance = allowance });
        doc.FlatBox = new Box3 { Min = new Point3(0, 0, -2), Max = new Point3(40, 63.28, 0) };
        return doc;
    }

    [Fact]
    public void Flattening_turns_the_flat_pattern_on_and_at_half_leaves_the_volume_then_folding_turns_it_off()
    {
        var doc = Flanged();
        double folded = doc.MassProperties!.VolumeMm3;
        var flat = Flatten(true);
        Assert.Equal(SheetState.Flattened, flat.State);
        Assert.False(flat.Unchanged);
        Assert.Equal(folded, flat.Change.VolumeAfterMm3, 6);
        var changed = Assert.Single(flat.Change.Changed);
        Assert.Equal(("Flat-Pattern1", "suppressed", "suppressed (folded)", "unsuppressed (flattened)"), (changed.Name, changed.Property, changed.Before, changed.After));
        Assert.Contains("the part reads flattened from its Flat-Pattern feature", flat.Checks);
        Assert.Contains("no bend has a cylindrical face left", flat.Checks);
        Assert.Contains("the flattened part is 2 mm across its base face, the sheet's thickness", flat.Checks);
        Assert.Contains(flat.Checks, c => c.StartsWith("the volume changed by 0 mm³, the bend arithmetic's 0 mm³ (developing 1 bends"));
        Assert.NotNull(flat.Flat);
        Assert.Equal(SheetState.Flattened, SheetMetalParts.State(doc));
        var back = Flatten(false);
        Assert.Equal(SheetState.Folded, back.State);
        Assert.Equal(folded, back.Change.VolumeAfterMm3, 6);
        Assert.Contains(back.Checks, c => c.StartsWith("the volume changed by 0 mm³, the bend arithmetic's 0 mm³ (folding 1 bends"));
        Assert.Equal(new[] { ("Flat-Pattern1", false), ("Flat-Pattern1", true) }, doc.Suppressions);
    }

    [Fact]
    public void Away_from_half_the_volume_moves_by_exactly_the_bend_arithmetic_and_back()
    {
        var doc = Flanged(new BendAllowance { KFactor = 0.1 });
        double folded = doc.MassProperties!.VolumeMm3;
        // 40 mm of bend, 2 mm thick, developing to π/2 × 3.2 rather than its middle arc π/2 × 4.
        double change = 40 * 2 * Math.PI / 2 * (3.2 - 4);
        Assert.Equal(folded + change, Flatten(true).Change.VolumeAfterMm3, 6);
        Assert.Equal(folded, Flatten(false).Change.VolumeAfterMm3, 6);
    }

    [Fact]
    public void A_part_already_in_the_state_asked_is_left_as_it_is()
    {
        var doc = Flanged();
        var same = Flatten(false);
        Assert.True(same.Unchanged);
        Assert.Equal(SheetState.Folded, same.State);
        Assert.Contains("the part was already folded; nothing was changed", same.Checks);
        Assert.Empty(same.Change.Changed);
        Assert.Empty(doc.Suppressions);
    }

    [Theory]
    [InlineData("volume", "volume_developed")]
    [InlineData("faces", "bend_faces")]
    [InlineData("thick", "flat_thickness")]
    public void A_flat_pattern_that_is_not_the_bends_developed_is_turned_off_again(string fault, string check)
    {
        var doc = Flanged();
        double folded = doc.MassProperties!.VolumeMm3;
        if (fault == "volume") doc.FlattenVolumeError = 3;
        if (fault == "faces") doc.FlattenKeepsBendFaces = true;
        if (fault == "thick") doc.FlatBox = new Box3 { Min = new Point3(0, 0, -27), Max = new Point3(40, 20, 0) };
        var e = Fails(() => Flatten(true));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.Equal(SheetState.Folded, SheetMetalParts.State(doc));
        Assert.Equal(folded, doc.MassProperties.VolumeMm3, 9);
    }

    [Fact]
    public void A_flat_pattern_SOLIDWORKS_will_not_turn_on_fails()
    {
        var doc = Flanged();
        doc.SuppressRefused = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Flatten(true)).Code);
        Assert.Equal(SheetState.Folded, SheetMetalParts.State(doc));
    }

    [Fact]
    public void A_part_that_is_not_sheet_metal_is_refused()
    {
        Part();
        Block();
        Assert.Equal("sheet_metal", Fails(() => Flatten(true)).Error.Details!["needs"]);
    }
}

public class SheetMetalUnfoldFoldTests : SheetMetalBendTestBase
{
    internal SheetMetalFeatureResult Unfold(SheetMetalUnfoldRequest request) => Run(new SheetMetalUnfoldOperation(Services), request);

    internal SheetMetalFeatureResult Fold(SheetMetalFoldRequest request) => Run(new SheetMetalFoldOperation(Services), request);

    /// <summary>The tab with a 25 mm flange (EdgeBend1) on its 40 mm edge at y = 20, and another (EdgeBend2) on the one at y = 0 when asked.</summary>
    FakeDocument Flanged(BendAllowance? allowance = null, bool two = false)
    {
        var doc = Tabbed();
        var edges = new List<string> { Top(doc) };
        if (two) edges.Add(Bottom(doc));
        EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = edges, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp, Allowance = allowance });
        return doc;
    }

    [Fact]
    public void Unfold_develops_a_bend_keeping_the_base_face_and_fold_folds_it_back_on_the_same_face()
    {
        var doc = Flanged();
        double folded = doc.MassProperties!.VolumeMm3;
        var unfold = Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc), Bends = new List<string> { "EdgeBend1" } });
        Assert.Equal("UnFold1", unfold.Feature.Name);
        Assert.Equal("EdgeBend1", Assert.Single(unfold.Sheet.Bends).Name);
        Assert.Equal(folded, unfold.Change.VolumeAfterMm3, 6);
        Assert.Contains("EdgeBend1 unfolded: no cylindrical face left", unfold.Checks);
        Assert.Contains(unfold.Checks, c => c.StartsWith("the fixed face stayed put"));
        Assert.Contains(unfold.Checks, c => c.StartsWith("the volume changed by 0 mm³, the bend arithmetic's 0 mm³ (developing 1 bends"));
        var (face, bends, isUnfold) = doc.FoldCalls.Single();
        Assert.True(isUnfold);
        Assert.Same(doc.SheetFaceList[0], face);
        Assert.Equal(new[] { "EdgeBend1" }, bends.Cast<FakeEntity>().Select(b => b.Info.Name));
        var fold = Fold(new SheetMetalFoldRequest());
        Assert.Equal("Fold1", fold.Feature.Name);
        Assert.Contains("EdgeBend1 folded: their cylindrical faces are back", fold.Checks);
        Assert.Same(doc.SheetFaceList[0], doc.FoldCalls.Last().FixedFace);
        Assert.Equal(folded, fold.Change.VolumeAfterMm3, 6);
        Assert.Empty(SheetMetalParts.Unfolded(doc));
    }

    [Fact]
    public void Away_from_half_unfold_and_fold_move_the_volume_by_the_bend_arithmetic_each_way()
    {
        var doc = Flanged(new BendAllowance { KFactor = 0.1 });
        double folded = doc.MassProperties!.VolumeMm3;
        double change = 40 * 2 * Math.PI / 2 * (3.2 - 4);
        Assert.Equal(folded + change, Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) }).Change.VolumeAfterMm3, 6);
        Assert.Equal(folded, Fold(new SheetMetalFoldRequest { FixedFace = Base(doc) }).Change.VolumeAfterMm3, 6);
    }

    [Fact]
    public void Without_bends_every_folded_bend_is_unfolded_and_every_unfolded_one_folded()
    {
        var doc = Flanged(two: true);
        var unfold = Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) });
        Assert.Equal(new[] { "EdgeBend1", "EdgeBend2" }, unfold.Sheet.Bends.Select(b => b.Name));
        var fold = Fold(new SheetMetalFoldRequest { Bends = new List<string> { "EdgeBend2" } });
        Assert.Equal("EdgeBend2", Assert.Single(fold.Sheet.Bends).Name);
        Assert.Equal(new[] { "EdgeBend1" }, SheetMetalParts.Unfolded(doc).Keys);
        var rest = Fold(new SheetMetalFoldRequest());
        Assert.Equal("EdgeBend1", Assert.Single(rest.Sheet.Bends).Name);
    }

    [Fact]
    public void A_bend_named_by_its_reference_is_found_and_what_is_not_a_bend_is_refused()
    {
        var doc = Flanged(two: true);
        string reference = Ref(doc, doc.FeatureByName("EdgeBend2")!);
        Assert.Equal("EdgeBend2", Assert.Single(Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc), Bends = new List<string> { reference } }).Sheet.Bends).Name);
        string Field(Func<SheetMetalFeatureResult> call) => (string)Fails(() => call()).Error.Details!["field"]!;
        Assert.Equal("/bends/0", Field(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc), Bends = new List<string> { "Base-Flange1" } })));
        Assert.Equal("/bends/1", Field(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc), Bends = new List<string> { "EdgeBend1", "EdgeBend1" } })));
        Assert.Equal("/bends/0", Field(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc), Bends = new List<string> { "EdgeBend2" } })));
        Assert.Equal("/bends/0", Field(() => Fold(new SheetMetalFoldRequest { Bends = new List<string> { "EdgeBend1" } })));
        Assert.Equal("fixed_face", Field(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Ref(doc, doc.FakeBends[0].Faces[0]) })));
    }

    [Fact]
    public void Folding_needs_something_unfolded_and_a_fixed_face_when_SOLIDWORKS_does_not_give_the_unfolds()
    {
        var doc = Flanged();
        Assert.Equal("bends", Fails(() => Fold(new SheetMetalFoldRequest())).Error.Details!["field"]);
        Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) });
        doc.FoldsHideFixedFace = true;
        var e = Fails(() => Fold(new SheetMetalFoldRequest()));
        Assert.Equal("fixed_face", e.Error.Details!["field"]);
        Assert.Contains("did not give the face UnFold1 kept put", e.Message);
        Assert.Equal("Fold1", Fold(new SheetMetalFoldRequest { FixedFace = Base(doc) }).Feature.Name);
    }

    [Theory]
    [InlineData("nothing", "bend_faces")]
    [InlineData("fixed", "fixed_face")]
    public void An_unfold_that_did_not_unfold_or_moved_the_fixed_face_is_deleted(string fault, string check)
    {
        var doc = Flanged();
        if (fault == "nothing") doc.FoldsDoNothing = true;
        if (fault == "fixed") doc.FixedFaceMoves = 2;
        var e = Fails(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == SheetMetalParts.UnfoldType);
        Assert.Empty(SheetMetalParts.Unfolded(doc));
        Assert.Equal(2, SheetMetalParts.Cylinders(doc, doc.SheetMetal.Bends().Single()).Count);
    }

    [Fact]
    public void A_fold_that_did_not_fold_is_deleted_and_the_bend_stays_unfolded()
    {
        var doc = Flanged();
        Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) });
        doc.FoldsDoNothing = true;
        Assert.Equal("bend_faces", Fails(() => Fold(new SheetMetalFoldRequest())).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == SheetMetalParts.FoldType);
        Assert.Equal(new[] { "EdgeBend1" }, SheetMetalParts.Unfolded(doc).Keys);
    }

    [Fact]
    public void SOLIDWORKS_making_no_feature_fails_and_a_flattened_part_is_refused()
    {
        var doc = Flanged();
        doc.FeatureRefuses = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) })).Code);
        doc.FeatureRefuses = false;
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        Assert.Equal("flattened", Fails(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = Base(doc) })).Error.Details!["state"]);
        Assert.Equal("flattened", Fails(() => Fold(new SheetMetalFoldRequest())).Error.Details!["state"]);
    }
}

/// <summary>
/// An extruded L of 60 × 40 mm with a 2 mm wall, 50 mm deep, its outer corner at the origin: the inner face of its
/// 60 mm leg (the floor, facing +y) and of its 40 mm leg (the wall, facing +x) meet at the inner corner (2, 2); the
/// outer faces meet at (0, 0). For a U, a second wall faces -x at x = 58.
/// </summary>
public class SheetMetalConvertTests : SheetMetalBendTestBase
{
    internal SheetMetalFeatureResult Convert(SheetMetalConvertRequest request) => Run(new SheetMetalConvertOperation(Services), request);

    FakeDocument _doc = null!;
    FakeEntity _floor = null!, _wall = null!, _inner = null!, _outer = null!, _bottom = null!;

    FakeEntity Face(Vector3 normal, Point3 centre) =>
        _doc.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, Normal = normal, Origin = centre, DistanceMm = 0, AreaMm2 = 1000 });

    void L()
    {
        _doc = Part();
        Block();
        _doc.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 50) };
        _floor = Face(new Vector3(0, 1, 0), new Point3(31, 2, 25));
        _wall = Face(new Vector3(1, 0, 0), new Point3(2, 21, 25));
        _bottom = Face(new Vector3(0, -1, 0), new Point3(30, 0, 25));
        var back = Face(new Vector3(-1, 0, 0), new Point3(0, 20, 25));
        _inner = _doc.Corner(_floor, _wall, new Point3(2, 2, 25), new Vector3(0, 0, 1), 50);
        _outer = _doc.Corner(_bottom, back, new Point3(0, 0, 25), new Vector3(0, 0, 1), 50);
    }

    SheetMetalConvertRequest Request(params FakeEntity[] edges) => new SheetMetalConvertRequest
    {
        FixedFace = Ref(_doc, _floor),
        BendEdges = edges.Select(e => Ref(_doc, e)).ToList(),
        ThicknessMm = 2,
        BendRadiusMm = 3,
    };

    [Fact]
    public void An_L_becomes_sheet_metal_with_one_bend_at_its_inner_corner_and_its_box_as_it_was()
    {
        L();
        var made = Convert(Request(_inner));
        Assert.Equal("Convert-Solid1", made.Feature.Name);
        Assert.Equal(new[] { "Sheet-Metal1", "Flat-Pattern1", "Convert-Solid1" }, made.Change.Created.Select(c => c.Name));
        var bend = Assert.Single(made.Sheet.Bends);
        Assert.Equal(3, bend.InnerRadiusMm);
        Assert.Equal(2, made.Sheet.ThicknessMm);
        Assert.Contains(made.Checks, c => c.Contains("measures 2 mm between the sheet's two faces"));
        Assert.Contains("1 bends, one per edge, at inner radius 3 mm and K 0.5", made.Checks);
        Assert.Contains("the part's box is as it was within 0 mm (the thickness is 2 mm)", made.Checks);
        var (face, edges, spec) = _doc.Converts.Single();
        Assert.Same(_floor, face);
        Assert.Same(_inner, Assert.Single(edges));
        Assert.Equal((2.0, 3.0, false, false, 2.0), (spec.ThicknessMm, spec.BendRadiusMm, spec.ReverseThickness, spec.KeepBody, spec.GapMm));
        Assert.Empty(_doc.DefaultEdits);
    }

    [Fact]
    public void A_U_converts_with_a_bend_per_edge_and_an_allowance_given_becomes_the_parts_default()
    {
        L();
        var right = Face(new Vector3(-1, 0, 0), new Point3(58, 21, 25));
        var second = _doc.Corner(_floor, right, new Point3(58, 2, 25), new Vector3(0, 0, 1), 50);
        var request = Request(_inner, second);
        request.Allowance = new BendAllowance { KFactor = 0.1 };
        request.KeepBody = true;
        var made = Convert(request);
        Assert.Equal(2, made.Sheet.Bends.Count);
        Assert.All(made.Sheet.Bends, b => Assert.Equal(0.1, b.Allowance.KFactor));
        Assert.Equal(0.1, _doc.DefaultEdits.Single().Allowance!.KFactor);
        Assert.True(_doc.Converts.Single().Spec.KeepBody);
    }

    [Fact]
    public void What_cannot_convert_is_refused_before_SOLIDWORKS_sees_it()
    {
        L();
        string Field(SheetMetalConvertRequest r) => (string)Fails(() => Convert(r)).Error.Details!["field"]!;
        var r = Request(_inner);
        r.ThicknessMm = 0;
        Assert.Equal("thickness_mm", Field(r));
        r = Request(_inner);
        r.BendRadiusMm = 0;
        Assert.Equal("bend_radius_mm", Field(r));
        Assert.Equal("bend_edges", Field(Request()));
        Assert.Equal("/bend_edges/0", Field(Request(_outer)));
        var arc = _doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Feature = "Boss-Extrude1" });
        Assert.Equal("/bend_edges/0", Field(Request(arc)));
        r = Request(_inner);
        r.FixedFace = Ref(_doc, _bottom);
        Assert.Equal("fixed_face", Field(r));
        Assert.Contains("borders none of the bend edges", Fails(() => Convert(r)).Message);
        Assert.Empty(_doc.Converts);
    }

    [Fact]
    public void A_part_that_is_already_sheet_metal_is_refused()
    {
        var doc = Tabbed();
        var e = Fails(() => Convert(new SheetMetalConvertRequest { FixedFace = Base(doc), BendEdges = new List<string> { Top(doc) }, ThicknessMm = 2, BendRadiusMm = 3 }));
        Assert.Equal("document", e.Error.Details!["field"]);
        Assert.Contains("already sheet metal", e.Message);
    }

    [Theory]
    [InlineData("edge", "bends")]
    [InlineData("box", "box")]
    public void A_conversion_that_is_not_what_was_asked_is_deleted_whole(string fault, string check)
    {
        L();
        var right = Face(new Vector3(-1, 0, 0), new Point3(58, 21, 25));
        var second = _doc.Corner(_floor, right, new Point3(58, 2, 25), new Vector3(0, 0, 1), 50);
        if (fault == "edge") _doc.ConvertDropsAnEdge = true;
        if (fault == "box") _doc.ConvertBoxGrowth = 5;
        var e = Fails(() => Convert(Request(_inner, second)));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.DoesNotContain(_doc.Tree, f => f.Type == SheetMetalParts.SheetMetalType || f.Type == SheetMetalParts.FlatPatternType || f.Type == "SolidToSheetMetal");
        Assert.False(SheetMetalParts.IsSheetMetal(_doc));
        Assert.Equal(0, _doc.SheetBodies);
        Assert.Empty(_doc.SheetMetal.Bends());
        Assert.Equal(60, _doc.ExactBox!.Max.X);
    }

    [Fact]
    public void SOLIDWORKS_converting_nothing_fails()
    {
        L();
        _doc.FeatureRefuses = true;
        var e = Fails(() => Convert(Request(_inner)));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("SOLIDWORKS made no conversion to sheet metal", e.Message);
    }
}
