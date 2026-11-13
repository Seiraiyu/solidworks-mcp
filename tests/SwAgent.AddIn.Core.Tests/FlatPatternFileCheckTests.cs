using System;
using System.IO;
using System.Linq;
using System.Text;
using SwAgent.Contracts;
using Xunit;
using static SwAgent.AddIn.Core.Tests.DxfFileTests;

namespace SwAgent.AddIn.Core.Tests;

public class FlatPatternFileCheckTests
{
    readonly string _folder = TempDir.Create();
    readonly DateTime _started = DateTime.UtcNow;

    string Write(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>CADapter's box of four 20 mm flanges: 136.5664 × 96.5664 flat, as the cut list rounds it, with four bends.</summary>
    static FlatPatternExpectation Box(bool bendLines = true) => new FlatPatternExpectation
    {
        LengthMm = 136.57,
        WidthMm = 96.57,
        ToleranceMm = 0.015,
        MmPerUnit = 1,
        BendLines = bendLines,
        Bends = 4,
    };

    static object[][] BoxOutline(bool bendLines)
    {
        var outline = Rectangle(136.5664, 96.5664);
        return bendLines ? outline.Concat(new[] { 18.1416, 38.1416, 58.4248, 78.4248 }.Select(y => Line(0, y, 136.5664, y, "BENDLINES"))).ToArray() : outline;
    }

    static string CheckOf(SwAgentException e)
    {
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        return (string)e.Error.Details!["check"]!;
    }

    SwAgentException Fails(string path, FlatPatternFormat format, FlatPatternExpectation expected) =>
        Assert.Throws<SwAgentException>(() => FlatPatternFileCheck.Check(path, format, _started, expected));

    [Fact]
    public void A_DXF_whose_extents_are_the_flat_size_passes_with_its_bend_lines()
    {
        string path = Write("box.dxf", Drawing(Extents(0, 0, 136.5664, 96.5664), BoxOutline(true)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, Box());
        Assert.Equal("DXF", file.Header);
        Assert.Equal(136.5664, file.Extents!.Value.MaxX, 9);
        Assert.Equal("its entities", file.ExtentsFrom);
        Assert.Equal(4, file.BendLines);
        Assert.Contains("0 = 4", file.Layers);
        Assert.Contains("BENDLINES = 4", file.Layers);
        Assert.Contains(file.Checks, c => c.Contains("136.5664 × 96.5664 mm") && c.Contains("136.57 × 96.57 mm"));
        Assert.Contains(file.Checks, c => c.Contains("4 bend lines (lines outside the outline's chain) for 4 bends"));
    }

    [Fact]
    public void A_DXF_turned_a_quarter_still_matches()
    {
        string path = Write("turned.dxf", Drawing(Array.Empty<object>(), Rectangle(96.5664, 136.5664)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, Box(bendLines: false));
        Assert.Equal("its entities", file.ExtentsFrom);
        Assert.Contains(file.Checks, c => c == "no bend lines, as asked");
    }

    [Fact]
    public void Inches_are_converted_before_comparing()
    {
        double w = 136.5664 / 25.4, h = 96.5664 / 25.4;
        var header = Extents(0, 0, w, h).Concat(new object[] { 9, "$INSUNITS", 70, 1 }).ToArray();
        string path = Write("inches.dxf", Drawing(header, Rectangle(w, h)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, Box(bendLines: false));
        Assert.Equal(136.5664, file.Extents!.Value.MaxX, 6);
    }

    [Fact]
    public void Without_units_the_parts_own_unit_is_taken()
    {
        var expected = Box(bendLines: false);
        expected.MmPerUnit = 25.4;
        string path = Write("part-units.dxf", Drawing(Array.Empty<object>(), Rectangle(136.5664 / 25.4, 96.5664 / 25.4)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, expected);
        Assert.Contains(file.Checks, c => c.Contains("no $INSUNITS"));
    }

    [Fact]
    public void Units_it_does_not_know_are_refused()
    {
        var header = new object[] { 9, "$INSUNITS", 70, 13 };
        string path = Write("microns.dxf", Drawing(header, Rectangle(136.5664, 96.5664)));
        Assert.Equal("dxf_units", CheckOf(Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false))));
    }

    [Fact]
    public void Extents_off_the_flat_size_fail_naming_both()
    {
        string path = Write("off.dxf", Drawing(Array.Empty<object>(), Rectangle(136.6, 96.5664)));
        var e = Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false));
        Assert.Equal("flat_size", CheckOf(e));
        Assert.Contains("136.57 × 96.57", (string)e.Error.Details!["expected"]!);
        Assert.Contains("136.6 × 96.5664", (string)e.Error.Details["measured"]!);
    }

    [Fact]
    public void The_flat_size_is_the_entities_and_the_header_is_reported_only()
    {
        // The 6.D live run: ExportFlatPatternView wrote the letter sheet (279.4 × 215.9) as $EXTMIN/$EXTMAX, and the
        // exact flat as its entities.
        string path = Write("sheet.dxf", Drawing(Extents(0, 0, 279.4, 215.9), Rectangle(136.5664, 96.5664)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, Box(bendLines: false));
        Assert.Equal("its entities", file.ExtentsFrom);
        Assert.Equal(136.5664, file.Extents!.Value.Width, 9);
        Assert.Contains(file.Checks, c => c.Contains("$EXTMIN/$EXTMAX give 279.4 × 215.9 mm") && c.Contains("not compared"));
    }

    [Fact]
    public void Entities_off_the_flat_size_fail_even_when_the_header_matches()
    {
        string path = Write("stale.dxf", Drawing(Extents(0, 0, 136.5664, 96.5664), Rectangle(200, 96.5664)));
        var e = Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false));
        Assert.Equal("flat_size", CheckOf(e));
        Assert.Contains("200 × 96.5664", (string)e.Error.Details!["measured"]!);
    }

    [Fact]
    public void Lines_across_the_outline_on_layer_0_are_bend_lines_one_per_bend()
    {
        var expected = new FlatPatternExpectation { LengthMm = 100, WidthMm = 83.28, ToleranceMm = 0.015, MmPerUnit = 1, BendLines = true, Bends = 1 };
        var entities = Rectangle(100, 83.2832).Concat(new[] { Line(0, 58.1416, 100, 58.1416, "0", "Continuous") }).ToArray();
        string path = Write("layer0.dxf", Drawing(Array.Empty<object>(), entities));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, expected);
        Assert.Equal(1, file.BendLines);
        Assert.Contains(file.Checks, c => c.Contains("1 bend lines (lines outside the outline's chain) for 1 bends"));
    }

    [Fact]
    public void Fewer_bend_lines_than_bends_fail()
    {
        var entities = Rectangle(136.5664, 96.5664).Concat(new[] { Line(0, 18.1416, 136.5664, 18.1416) }).ToArray();
        string path = Write("one-of-four.dxf", Drawing(Array.Empty<object>(), entities));
        var e = Fails(path, FlatPatternFormat.Dxf, Box());
        Assert.Equal("bend_lines", CheckOf(e));
        Assert.Contains("has 1 for the part's 4 bends", e.Message);
    }

    [Fact]
    public void Bend_lines_asked_for_and_missing_fail_listing_the_layers()
    {
        string path = Write("no-bends.dxf", Drawing(Array.Empty<object>(), BoxOutline(false)));
        var e = Fails(path, FlatPatternFormat.Dxf, Box());
        Assert.Equal("bend_lines", CheckOf(e));
        Assert.Contains("0 = 4", (string)e.Error.Details!["measured"]!);
    }

    [Fact]
    public void With_sketches_drawn_lines_left_without_bend_lines_are_reported_not_failed()
    {
        // A visible sketch's free lines look like bend lines, so "none" cannot be told when sketches are drawn.
        var expected = Box(bendLines: false);
        expected.Sketches = true;
        string path = Write("sketch-lines.dxf", Drawing(Array.Empty<object>(), BoxOutline(true)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, expected);
        Assert.Contains(file.Checks, c => c.Contains("4 free lines") && c.Contains("sketches"));
    }

    [Fact]
    public void Bend_lines_present_when_left_out_fail()
    {
        string path = Write("bends.dxf", Drawing(Array.Empty<object>(), BoxOutline(true)));
        Assert.Equal("bend_lines", CheckOf(Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false))));
    }

    [Fact]
    public void A_part_without_bends_needs_no_bend_lines()
    {
        var expected = new FlatPatternExpectation { LengthMm = 100, WidthMm = 60, ToleranceMm = 0.015, MmPerUnit = 1, BendLines = true, Bends = 0 };
        string path = Write("tab.dxf", Drawing(Array.Empty<object>(), Rectangle(100, 60)));
        var file = FlatPatternFileCheck.Check(path, FlatPatternFormat.Dxf, _started, expected);
        Assert.Contains(file.Checks, c => c.Contains("no bends"));
    }

    [Fact]
    public void A_DXF_that_does_not_parse_fails_its_header_check()
    {
        string path = Write("broken.dxf", "0\r\nSECTION\r\n2\r\n");
        Assert.Equal("file_header", CheckOf(Fails(path, FlatPatternFormat.Dxf, Box())));
    }

    [Fact]
    public void A_DXF_with_nothing_to_measure_fails()
    {
        string path = Write("empty.dxf", Drawing(Array.Empty<object>()));
        Assert.Equal("dxf_extents", CheckOf(Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false))));
    }

    [Fact]
    public void A_DWG_needs_its_version_header_and_some_size()
    {
        var bytes = new byte[4096];
        Encoding.ASCII.GetBytes("AC1032").CopyTo(bytes, 0);
        var file = FlatPatternFileCheck.Check(Write("box.dwg", bytes), FlatPatternFormat.Dwg, _started, Box());
        Assert.Equal("AC1032", file.Header);
        Assert.Null(file.Extents);
        Assert.Contains(file.Checks, c => c.Contains("not read"));

        Assert.Equal("file_header", CheckOf(Fails(Write("text.dwg", "0\r\nSECTION\r\n"), FlatPatternFormat.Dwg, Box())));
        var small = new byte[100];
        Encoding.ASCII.GetBytes("AC1027").CopyTo(small, 0);
        Assert.Equal("dwg_size", CheckOf(Fails(Write("small.dwg", small), FlatPatternFormat.Dwg, Box())));
    }

    [Fact]
    public void A_file_written_before_the_call_is_not_this_calls()
    {
        string path = Write("old.dxf", Drawing(Array.Empty<object>(), Rectangle(136.5664, 96.5664)));
        File.SetLastWriteTimeUtc(path, _started.AddMinutes(-5));
        Assert.Equal("file_written", CheckOf(Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false))));
    }
}
