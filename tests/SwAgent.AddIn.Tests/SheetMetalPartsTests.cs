using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SheetMetalPartsTests : FeatureTestBase
{
    /// <summary>A part made sheet metal straight through the fake: Sketch1 (40 × 20) as a 2 mm flat tab.</summary>
    internal FakeDocument SheetPart()
    {
        var doc = Part();
        doc.SheetMetal.BaseFlange(doc.FeatureByName("Sketch1")!, new BaseFlangeSpec { ThicknessMm = 2, BendRadiusMm = 3 });
        return doc;
    }

    static SwAgentException Throws(Action action) => Assert.Throws<SwAgentException>(action);

    [Fact]
    public void A_part_is_sheet_metal_by_its_SheetMetal_feature_and_flattened_by_its_flat_pattern()
    {
        var doc = Part();
        Assert.False(SheetMetalParts.IsSheetMetal(doc));
        doc.SheetMetal.BaseFlange(doc.FeatureByName("Sketch1")!, new BaseFlangeSpec { ThicknessMm = 2, BendRadiusMm = 3 });
        Assert.True(SheetMetalParts.IsSheetMetal(doc));
        Assert.Equal(SheetState.Folded, SheetMetalParts.State(doc));
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        Assert.Equal(SheetState.Flattened, SheetMetalParts.State(doc));
    }

    [Fact]
    public void An_ordinary_part_is_the_wrong_document_type_and_says_what_it_needs()
    {
        var doc = Part();
        var e = Throws(() => SheetMetalParts.Require(doc, "sheetmetal.read"));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Equal("sheet_metal", e.Error.Details!["needs"]);
        Assert.Equal("part", e.Error.Details["type"]);
        Assert.Contains("sw_sheetmetal_base_flange", e.Message);
        doc.SheetMetal.BaseFlange(doc.FeatureByName("Sketch1")!, new BaseFlangeSpec { ThicknessMm = 2, BendRadiusMm = 3 });
        Assert.Equal(2, SheetMetalParts.Require(doc, "sheetmetal.read").ThicknessMm);
    }

    [Fact]
    public void A_flattened_part_is_refused_a_change_and_nothing_folds_it()
    {
        var doc = SheetPart();
        SheetMetalParts.RequireFolded(doc, "sheetmetal.set_parameters");
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        var e = Throws(() => SheetMetalParts.RequireFolded(doc, "sheetmetal.set_parameters"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("flattened", e.Error.Details!["state"]);
        Assert.Equal(SheetState.Flattened, SheetMetalParts.State(doc));
    }

    [Theory]
    [InlineData(null, null, null, "allowance")]
    [InlineData(0.5, 1.0, null, "allowance")]
    [InlineData(0.0, null, null, "allowance/k_factor")]
    [InlineData(1.5, null, null, "allowance/k_factor")]
    [InlineData(null, 0.0, null, "allowance/allowance_mm")]
    [InlineData(null, null, -1.0, "allowance/deduction_mm")]
    public void An_allowance_needs_exactly_one_value_in_range(double? k, double? allowance, double? deduction, string field)
    {
        var e = Throws(() => SheetMetalParts.Allowance(new BendAllowance { KFactor = k, AllowanceMm = allowance, DeductionMm = deduction }, "allowance"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }

    [Fact]
    public void A_good_allowance_is_copied_and_a_relief_ratio_is_checked()
    {
        Assert.Equal(1, SheetMetalParts.Allowance(new BendAllowance { KFactor = 1 }, "allowance").KFactor);
        Assert.Equal(0, SheetMetalParts.Allowance(new BendAllowance { DeductionMm = 0 }, "allowance").DeductionMm);
        Assert.Equal(0.5, SheetMetalParts.ReliefRatio(null, "relief_ratio"));
        Assert.Equal("relief_ratio", Throws(() => SheetMetalParts.ReliefRatio(0.01, "relief_ratio")).Error.Details!["field"]);
        Assert.True(SheetMetalParts.Same(new BendAllowance { KFactor = 0.4 }, new BendAllowance { KFactor = 0.4000000001 }));
        Assert.False(SheetMetalParts.Same(new BendAllowance { KFactor = 0.4 }, new BendAllowance { AllowanceMm = 0.4 }));
    }

    [Fact]
    public void The_thickness_is_read_back_and_measured_between_the_sheets_faces()
    {
        var doc = SheetPart();
        var checks = new List<string>();
        SheetMetalParts.ThicknessIs(doc, 2, checks);
        Assert.Contains("measures 2 mm between the sheet's two faces", Assert.Single(checks));
        Assert.Equal(2, SheetMetalParts.MeasuredThickness(doc)!.Value, 9);
        Assert.Equal(1000, doc.Describe(SheetMetalParts.BaseFace(doc)!).AreaMm2);
    }

    [Fact]
    public void A_thickness_off_in_the_definition_or_the_geometry_fails_its_check()
    {
        var doc = SheetPart();
        doc.ThicknessError = 0.1;
        Assert.Equal("thickness", Throws(() => SheetMetalParts.ThicknessIs(doc, 2, new List<string>())).Error.Details!["check"]);
        doc.ThicknessError = 0;
        doc.FaceGapError = 0.1;
        doc.SetDefaults(new SheetDefaultsEdit { ThicknessMm = 2 });
        Assert.Equal("thickness_measured", Throws(() => SheetMetalParts.ThicknessIs(doc, 2, new List<string>())).Error.Details!["check"]);
    }

    [Fact]
    public void The_flat_size_comes_from_the_cut_list_in_the_documents_units()
    {
        var props = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Bounding Box Length", "4"),
            new KeyValuePair<string, string>("Bounding Box Width", " 2.5 "),
        };
        var size = SheetMetalParts.FlatSize(props, 25.4)!.Value;
        Assert.Equal(101.6, size.LengthMm, 9);
        Assert.Equal(63.5, size.WidthMm, 9);
        Assert.Null(SheetMetalParts.FlatSize(props.Take(1).ToList(), 1));
        Assert.Null(SheetMetalParts.FlatSize(new List<KeyValuePair<string, string>> { props[0], new KeyValuePair<string, string>("Bounding Box Width", "1'2\"") }, 1));
    }

    [Fact]
    public void A_bend_is_described_with_its_reference_and_its_width_along_its_axis()
    {
        var doc = Part();
        Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "a", Start = P(0, 0), End = P(0, 30) }, new SketchLine { Id = "b", Start = P(0, 30), End = P(50, 30) } } });
        doc.SheetMetal.BaseFlange(doc.FeatureByName("Sketch2")!, new BaseFlangeSpec { ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 20 });
        var bend = Assert.Single(SheetMetalParts.Describe(Services.References, doc, doc.SheetMetal.Bends()));
        Assert.Equal("BaseBend1", bend.Name);
        Assert.Equal("Base-Flange1", bend.Feature);
        Assert.StartsWith("r1.feature.", bend.Reference);
        Assert.Equal(90, bend.AngleDeg, 9);
        Assert.Equal(3, bend.InnerRadiusMm);
        Assert.Equal(20, bend.WidthMm);
        Assert.Equal(0.5, bend.Allowance.KFactor);
    }
}
