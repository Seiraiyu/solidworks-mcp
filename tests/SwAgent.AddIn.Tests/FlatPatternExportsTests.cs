using System.Collections.Generic;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class FlatPatternExportsTests
{
    static List<KeyValuePair<string, string>> CutList(string length, string width) => new List<KeyValuePair<string, string>>
    {
        new KeyValuePair<string, string>(SheetMetalParts.LengthProperty, length),
        new KeyValuePair<string, string>(SheetMetalParts.WidthProperty, width),
    };

    [Theory]
    [InlineData(true, false, 5)]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 13)]
    [InlineData(false, true, 9)]
    public void ExportToDWG2_takes_geometry_and_the_bend_lines_and_sketches_asked_for(bool bendLines, bool sketches, int options)
    {
        Assert.Equal(options, FlatPatternExports.DwgOptions(bendLines, sketches));
    }

    [Fact]
    public void Without_bend_lines_CADapters_call_is_tried_first_and_ExportToDWG2_after_it()
    {
        Assert.Equal(new[] { FlatPatternExports.FlatPatternView, FlatPatternExports.ToDwg2 }, FlatPatternExports.Methods(bendLines: false, sketches: false, unsaved: false));
    }

    [Fact]
    public void With_bend_lines_ExportToDWG2_is_tried_first()
    {
        // The 6.D live run: ExportFlatPatternView drew no bend lines on 2026 SP4 when asked for them; ExportToDWG2 did.
        Assert.Equal(new[] { FlatPatternExports.ToDwg2, FlatPatternExports.FlatPatternView }, FlatPatternExports.Methods(bendLines: true, sketches: false, unsaved: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sketches_are_written_by_ExportToDWG2_alone(bool bendLines)
    {
        // ExportFlatPatternView takes only "remove bend lines": it cannot add sketches.
        Assert.Equal(new[] { FlatPatternExports.ToDwg2 }, FlatPatternExports.Methods(bendLines, sketches: true, unsaved: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_part_with_unsaved_changes_is_written_by_ExportToDWG2_alone(bool bendLines)
    {
        // The 6.D live run: on a part with unsaved changes ExportFlatPatternView wrote 100 × 60 (the base alone) for a
        // flat of 100 × 82.0265; ExportToDWG2 wrote the part as it is now.
        Assert.Equal(new[] { FlatPatternExports.ToDwg2 }, FlatPatternExports.Methods(bendLines, sketches: false, unsaved: true));
    }

    [Fact]
    public void The_tolerance_is_a_hundredth_beyond_the_cut_lists_rounding()
    {
        Assert.Equal(0.015, FlatPatternExports.ToleranceMm(CutList("136.57", "96.57"), 1), 12);
        Assert.Equal(0.06, FlatPatternExports.ToleranceMm(CutList("136.6", "96.57"), 1), 12);
        Assert.Equal(0.01 + 0.00005 * 25.4, FlatPatternExports.ToleranceMm(CutList("5.3766", "3.8018"), 25.4), 12);
    }

    [Fact]
    public void Half_a_unit_in_the_last_place_is_what_rounding_can_hide()
    {
        var cutList = CutList("136.57", "100");
        Assert.Equal(0.005, SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.LengthProperty, 1), 12);
        Assert.Equal(0.5, SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.WidthProperty, 1), 12);
        Assert.Equal(0.505, SheetMetalParts.FlatRoundingMm(cutList, 1), 12);
    }

    [Fact]
    public void The_fake_writes_the_flat_pattern_the_cut_list_gives()
    {
        var doc = FakeDocument.Part("Part1", @"C:\scratch\part1.SLDPRT");
        doc.Sheet = new SheetDefaults { ThicknessMm = 2, BendRadiusMm = 3, Allowance = new BendAllowance { KFactor = 0.5 } };
        doc.CutListOverride = CutList("136.57", "96.57");
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swagent-tests", System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "box.dxf");
        // As the 6.D live run measured: ExportFlatPatternView writes the letter sheet as the header's extents, the
        // flat as its entities, and no bend lines.
        Assert.True(doc.ExportFlatPatternView(path, bendLines: true));
        var dxf = SwAgent.AddIn.Core.DxfFile.Read(path);
        Assert.Equal(279.4, dxf.HeaderExtents!.Value.Width, 9);
        Assert.Equal(136.57, dxf.EntityExtents!.Value.Width, 9);
        Assert.Equal(96.57, dxf.EntityExtents.Value.Height, 9);
        Assert.Equal(0, dxf.BendLines);
        Assert.Equal((FlatPatternExports.FlatPatternView, path, true, false), doc.FlatExports[0]);
    }

    [Fact]
    public void The_fakes_ExportToDWG2_draws_its_bend_lines_on_layer_0_across_the_outline()
    {
        var doc = FakeDocument.Part("Part1", @"C:\scratch\part1.SLDPRT");
        doc.Sheet = new SheetDefaults { ThicknessMm = 2, BendRadiusMm = 3, Allowance = new BendAllowance { KFactor = 0.5 } };
        doc.CutListOverride = CutList("136.57", "96.57");
        doc.FakeBends.Add(new FakeBend());
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swagent-tests", System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "box.dxf");
        Assert.True(doc.ExportToDwg2(path, doc.Document.Path!, FlatPatternExports.DwgOptions(bendLines: true, sketches: false)));
        var dxf = SwAgent.AddIn.Core.DxfFile.Read(path);
        Assert.Equal(1, dxf.BendLines);
        Assert.Equal(0, dxf.NamedBendLines);
        Assert.Equal(136.57, dxf.HeaderExtents!.Value.Width, 9);
    }
}
