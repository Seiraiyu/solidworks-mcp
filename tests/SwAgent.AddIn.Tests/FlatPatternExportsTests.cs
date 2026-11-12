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
    public void CADapters_call_is_tried_first_and_ExportToDWG2_after_it()
    {
        Assert.Equal(new[] { FlatPatternExports.FlatPatternView, FlatPatternExports.ToDwg2 }, FlatPatternExports.Methods(sketches: false));
    }

    [Fact]
    public void Sketches_are_written_by_ExportToDWG2_alone()
    {
        // ExportFlatPatternView takes only "remove bend lines": it cannot add sketches.
        Assert.Equal(new[] { FlatPatternExports.ToDwg2 }, FlatPatternExports.Methods(sketches: true));
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
        Assert.True(doc.ExportFlatPatternView(path, bendLines: true));
        var dxf = SwAgent.AddIn.Core.DxfFile.Read(path);
        Assert.Equal(136.57, dxf.HeaderExtents!.Value.Width, 9);
        Assert.Equal(96.57, dxf.HeaderExtents.Value.Height, 9);
        Assert.Equal((FlatPatternExports.FlatPatternView, path, true, false), doc.FlatExports[0]);
    }
}
