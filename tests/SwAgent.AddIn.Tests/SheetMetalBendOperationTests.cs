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
