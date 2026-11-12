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
        return bendLines ? outline.Concat(new[] { Line(0, 18.1416, 136.5664, 18.1416, "BENDLINES") }).ToArray() : outline;
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
        Assert.Equal("$EXTMIN/$EXTMAX", file.ExtentsFrom);
        Assert.Equal(1, file.BendLines);
        Assert.Contains("0 = 4", file.Layers);
        Assert.Contains("BENDLINES = 1", file.Layers);
        Assert.Contains(file.Checks, c => c.Contains("136.5664 × 96.5664 mm") && c.Contains("136.57 × 96.57 mm"));
        Assert.Contains(file.Checks, c => c.Contains("1 bend-line entities"));
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
        string path = Write("off.dxf", Drawing(Extents(0, 0, 136.6, 96.5664), Rectangle(136.6, 96.5664)));
        var e = Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false));
        Assert.Equal("flat_size", CheckOf(e));
        Assert.Contains("136.57 × 96.57", (string)e.Error.Details!["expected"]!);
        Assert.Contains("136.6 × 96.5664", (string)e.Error.Details["measured"]!);
    }

    [Fact]
    public void Header_extents_that_disagree_with_the_entities_are_reported_with_them()
    {
        string path = Write("stale.dxf", Drawing(Extents(0, 0, 200, 96.5664), Rectangle(136.5664, 96.5664)));
        var e = Fails(path, FlatPatternFormat.Dxf, Box(bendLines: false));
        Assert.Equal("flat_size", CheckOf(e));
        Assert.Contains("its entities reach 136.5664 × 96.5664", e.Message);
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
