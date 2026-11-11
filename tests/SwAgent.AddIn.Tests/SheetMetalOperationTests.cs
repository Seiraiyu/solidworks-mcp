using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Sheet-metal operations on a part whose Sketch1 is a 40 × 20 rectangle on the front plane.</summary>
public abstract class SheetMetalTestBase : FeatureTestBase
{
    internal SheetMetalFeatureResult BaseFlange(SheetMetalBaseFlangeRequest request) => Run(new SheetMetalBaseFlangeOperation(Services), request);

    /// <summary>Sketch1 as a 2 mm tab at R 3: 1600 mm³.</summary>
    internal SheetMetalFeatureResult Tab() => BaseFlange(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2, BendRadiusMm = 3 });

    /// <summary>A U of 30, 50 and 30 mm turning right twice, the next sketch.</summary>
    internal SketchCreateResult U() => Sketch(new SketchGeometry
    {
        Lines = new List<SketchLine>
        {
            new SketchLine { Id = "a", Start = P(0, 0), End = P(0, 30) },
            new SketchLine { Id = "b", Start = P(0, 30), End = P(50, 30) },
            new SketchLine { Id = "c", Start = P(50, 30), End = P(50, 0) },
        },
    });

    /// <summary>The U as a 2 mm channel 20 mm deep at R 3, material on the chain's left (outside the U).</summary>
    internal SheetMetalFeatureResult Channel(BendAllowance? allowance = null) => BaseFlange(new SheetMetalBaseFlangeRequest { Sketch = "Sketch2", ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 20, Allowance = allowance });
}

public class SheetMetalBaseFlangeTests : SheetMetalTestBase
{
    [Fact]
    public void A_closed_profile_makes_a_flat_tab_of_exactly_its_area_times_the_thickness()
    {
        var doc = Part();
        var made = Tab();
        Assert.Equal("Base-Flange1", made.Feature.Name);
        Assert.StartsWith("r1.feature.", made.Feature.Reference);
        Assert.Equal(1600, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("800 mm² × 2 mm"));
        Assert.Contains(made.Checks, c => c.Contains("measures 2 mm between the sheet's two faces"));
        Assert.Contains(made.Checks, c => c.Contains("allowance as K 0.5"));
        Assert.Equal(new[] { "Sheet-Metal1", "Flat-Pattern1", "Base-Flange1" }, made.Change.Created.Select(c => c.Name));
        Assert.Equal(2, made.Sheet.ThicknessMm);
        Assert.Equal(SheetState.Folded, made.Sheet.State);
        Assert.Empty(made.Sheet.Bends);
        var (_, spec) = doc.BaseFlanges.Single();
        Assert.Equal(0, spec.DepthMm);
        Assert.Equal(0.5, spec.Allowance.KFactor);
        Assert.Equal(ReliefType.Rectangular, spec.Relief);
        Assert.Equal(0.5, spec.ReliefRatio);
        Assert.False(spec.Reverse);
    }

    [Fact]
    public void An_open_chain_bends_at_each_corner_and_holds_the_bend_arithmetics_volume()
    {
        var doc = Part();
        U();
        var made = Channel(new BendAllowance { KFactor = 0.3 });
        Assert.Equal(20 * 2 * (98 + 4 * Math.PI), made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("material on the chain's left") && c.Contains("2 bends"));
        Assert.Equal(2, made.Sheet.Bends.Count);
        Assert.All(made.Sheet.Bends, b => Assert.Equal(90, b.AngleDeg, 6));
        Assert.All(made.Sheet.Bends, b => Assert.Equal(0.3, b.Allowance.KFactor));
        Assert.All(made.Sheet.Bends, b => Assert.Equal(20, b.WidthMm));
        Assert.Equal(20, doc.BaseFlanges.Single().Spec.DepthMm);
    }

    [Fact]
    public void Reversed_the_material_is_on_the_other_side_and_that_volume_is_found()
    {
        Part();
        U();
        var made = BaseFlange(new SheetMetalBaseFlangeRequest { Sketch = "Sketch2", ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 20, Reverse = true });
        Assert.Equal(20 * 2 * (90 + 4 * Math.PI), made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("material on the chain's right"));
    }

    [Fact]
    public void Requests_that_cannot_make_a_base_flange_name_their_field()
    {
        Part();
        U();
        string Field(SheetMetalBaseFlangeRequest r) => (string)Fails(() => BaseFlange(r)).Error.Details!["field"]!;
        Assert.Equal("thickness_mm", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", BendRadiusMm = 3 }));
        Assert.Equal("bend_radius_mm", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2 }));
        Assert.Equal("allowance", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2, BendRadiusMm = 3, Allowance = new BendAllowance() }));
        Assert.Equal("relief_ratio", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2, BendRadiusMm = 3, ReliefRatio = 5 }));
        Assert.Equal("depth_mm", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 10 }));
        Assert.Equal("depth_mm", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch2", ThicknessMm = 2, BendRadiusMm = 3 }));
        Assert.Equal("sketch", Field(new SheetMetalBaseFlangeRequest { Sketch = "Sketch9", ThicknessMm = 2, BendRadiusMm = 3 }));
    }

    [Fact]
    public void A_second_sheet_metal_body_is_refused_and_nothing_changes()
    {
        var doc = Part();
        Tab();
        U();
        var e = Fails(() => Channel());
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("document", e.Error.Details!["field"]);
        Assert.Contains("thickness belongs to the part", e.Message);
        Assert.Single(doc.BaseFlanges);
    }

    [Fact]
    public void A_base_flange_SOLIDWORKS_does_not_make_changes_nothing()
    {
        var doc = Part();
        doc.FeatureRefuses = true;
        var e = Fails(() => Tab());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("SOLIDWORKS made no base flange", e.Message);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "SheetMetal");
    }

    [Fact]
    public void A_thickness_that_does_not_read_back_rolls_back_all_three_features()
    {
        var doc = Part();
        doc.ThicknessError = 0.5;
        var e = Fails(() => Tab());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("thickness", e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "Base-Flange1", "Flat-Pattern1", "Sheet-Metal1" }, ((IEnumerable<object?>)e.Error.Details["rolled_back"]!).ToArray());
        Assert.DoesNotContain(doc.Tree, f => f.Type is "SheetMetal" or "FlatPattern" or "SMBaseFlange");
        Assert.Equal(0, doc.MassProperties!.VolumeMm3, 9);
        Assert.Equal(0, doc.SheetBodies);
        Assert.NotNull(doc.FeatureByName("Sketch1"));
    }

    [Fact]
    public void A_volume_off_the_profile_is_rolled_back()
    {
        var doc = Part();
        doc.FeatureVolumeError = 3;
        var e = Fails(() => Tab());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.Empty(doc.SheetMetal.Bends());
    }

    [Fact]
    public void A_feature_of_another_type_is_rolled_back_and_the_sketch_can_be_used_again()
    {
        var doc = Part();
        U();
        doc.FeatureTypeOverride = "Extrusion";
        Assert.Equal("feature_type", Fails(() => Channel()).Error.Details!["check"]);
        doc.FeatureTypeOverride = null;
        var made = Channel();
        Assert.Equal(2, made.Sheet.Bends.Count);
    }

    [Fact]
    public void An_open_profile_with_a_corner_too_tight_for_the_radius_cannot_have_its_volume_on_that_side()
    {
        Part();
        Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "a", Start = P(0, 0), End = P(0, 4) }, new SketchLine { Id = "b", Start = P(0, 4), End = P(30, 4) } } });
        var made = BaseFlange(new SheetMetalBaseFlangeRequest { Sketch = "Sketch2", ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 10 });
        Assert.Contains(made.Checks, c => c.Contains("left"));
    }
}

public class SheetMetalReadTests : SheetMetalTestBase
{
    internal SheetMetalReadResult Read(string? document = null) => Run(new SheetMetalReadOperation(Services), new SheetMetalReadRequest { Document = document });

    [Fact]
    public void An_ordinary_part_reads_as_not_sheet_metal_and_nothing_more()
    {
        Part();
        var read = Read();
        Assert.False(read.SheetMetal);
        Assert.Null(read.ThicknessMm);
        Assert.Null(read.State);
        Assert.Empty(read.Bends);
        Assert.Null(read.Flat);
        Assert.Equal("Part1", read.Document.Title);
    }

    [Fact]
    public void A_channel_reads_its_defaults_bends_base_face_and_flat_size()
    {
        var doc = Part();
        U();
        Channel(new BendAllowance { KFactor = 0.3 });
        var read = Read();
        Assert.True(read.SheetMetal);
        Assert.Equal(SheetState.Folded, read.State);
        Assert.Equal(2, read.ThicknessMm);
        Assert.Equal(3, read.BendRadiusMm);
        Assert.Equal(0.3, read.Allowance!.KFactor);
        Assert.Equal(ReliefType.Rectangular, read.Relief);
        Assert.Equal(0.5, read.ReliefRatio);
        Assert.Equal(1, read.SheetBodies);
        Assert.Equal(0, read.OtherBodies);
        Assert.Equal(new[] { "BaseBend1", "BaseBend2" }, read.Bends.Select(b => b.Name));
        Assert.StartsWith("r1.face.", read.BaseFace!.Reference);
        double ba = Math.PI / 2 * (3 + 0.3 * 2);
        Assert.Equal(doc.FlatStraightMm + 2 * ba, read.Flat!.LengthMm, 5);
        Assert.Equal(20, read.Flat.WidthMm, 9);
        Assert.Equal(2, read.Flat.Bends);
        Assert.Contains("Sheet Metal Thickness = 2", read.CutList);
    }

    [Fact]
    public void Cut_list_values_are_converted_from_the_parts_units_and_a_size_that_is_not_a_number_is_left_out()
    {
        var doc = Part();
        Tab();
        doc.UnitMm = 25.4;
        var read = Read();
        Assert.Equal(40, read.Flat!.LengthMm, 4);
        Assert.Equal(20, read.Flat.WidthMm, 4);
        doc.CutListOverride = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Bounding Box Length", "1'6\"") };
        read = Read();
        Assert.Null(read.Flat);
        Assert.Equal(new[] { "Bounding Box Length = 1'6\"" }, read.CutList);
    }

    [Fact]
    public void A_flattened_part_reads_as_flattened()
    {
        var doc = Part();
        Tab();
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        Assert.Equal(SheetState.Flattened, Read().State);
    }
}
