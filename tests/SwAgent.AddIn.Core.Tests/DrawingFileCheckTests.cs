using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwAgent.Contracts;
using Xunit;
using static SwAgent.AddIn.Core.Tests.DxfFileTests;

namespace SwAgent.AddIn.Core.Tests;

public class DrawingFileCheckTests
{
    readonly string _folder = TempDir.Create();
    readonly DateTime _started = DateTime.UtcNow;

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string Write(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    /// <summary>The front view of the 60 × 40 block at 1:1, centred at (120, 180) on an A3 sheet.</summary>
    static SheetExpectation Sheet1(params ViewExpectation[] views) => new SheetExpectation
    {
        Name = "Sheet1",
        WidthMm = 420,
        HeightMm = 297,
        Views = views.Length > 0 ? views.ToList() : new List<ViewExpectation> { Front() },
    };

    static ViewExpectation Front(double scale = 1) => new ViewExpectation
    {
        Name = "Drawing View1",
        Extents = new SheetBox { Min = new SheetPoint(90, 160), Max = new SheetPoint(150, 200) },
        Scale = scale,
    };

    static SheetExpectation A4Portrait() => new SheetExpectation { Name = "Details", WidthMm = 210, HeightMm = 297 };

    static string CheckOf(SwAgentException e)
    {
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        return (string)e.Error.Details!["check"]!;
    }

    // ---- PDF ----

    [Fact]
    public void A_pdf_has_a_page_per_sheet_each_its_sheets_size()
    {
        string path = Write("block.pdf", PdfFileTests.Simple("[0 0 1190.55 841.89]", "[0 0 595.276 841.89]"));
        var file = DrawingFileCheck.Pdf(path, _started, new[] { Sheet1(), A4Portrait() });
        Assert.Equal("%PDF-1.4", file.Header);
        Assert.Equal(new[] { "Sheet1", "Details" }, file.Pages.Select(p => p.Sheet));
        Assert.Equal(420, file.Pages[0].Page.WidthMm, 2);
        Assert.Contains(file.Checks, c => c.Contains("2 pages for 2 sheets"));
        Assert.Contains(file.Checks, c => c.StartsWith("page 2 (Details) 210 × 297 mm", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_turned_from_its_sheet_still_matches_and_is_said_to_be()
    {
        string path = Write("block.pdf", PdfFileTests.Simple("[0 0 841.89 1190.55]"));
        var file = DrawingFileCheck.Pdf(path, _started, new[] { Sheet1() });
        Assert.Contains(file.Checks, c => c.Contains("turned"));
    }

    [Fact]
    public void A_pdf_with_a_page_missing_is_refused()
    {
        string path = Write("block.pdf", PdfFileTests.Simple("[0 0 1190.55 841.89]"));
        var e = Assert.Throws<SwAgentException>(() => DrawingFileCheck.Pdf(path, _started, new[] { Sheet1(), A4Portrait() }));
        Assert.Equal("pdf_pages", CheckOf(e));
        Assert.Equal("2", e.Error.Details!["expected"]);
        Assert.Equal("1", e.Error.Details["measured"]);
    }

    [Fact]
    public void A_page_of_another_size_is_refused_naming_its_sheet()
    {
        string path = Write("block.pdf", PdfFileTests.Simple("[0 0 612 792]"));
        var e = Assert.Throws<SwAgentException>(() => DrawingFileCheck.Pdf(path, _started, new[] { Sheet1() }));
        Assert.Equal("page_size", CheckOf(e));
        Assert.Contains("Sheet1", e.Message);
        Assert.Contains("215.9 × 279.4 mm", e.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_pdf_is_refused()
    {
        string path = Write("block.pdf", "PK\u0003\u0004 not a pdf");
        Assert.Equal("file_header", CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Pdf(path, _started, new[] { Sheet1() }))));
    }

    [Fact]
    public void A_pdf_written_before_the_call_is_not_this_calls()
    {
        string path = Write("block.pdf", PdfFileTests.Simple("[0 0 1190.55 841.89]"));
        File.SetLastWriteTimeUtc(path, _started.AddMinutes(-5));
        Assert.Equal("file_written", CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Pdf(path, _started, new[] { Sheet1() }))));
    }

    // ---- DXF ----

    /// <summary>A3's border, the front view's four edges, its hole, and a centre line that runs out of the view.</summary>
    static string Sheet1Dxf(double viewShiftMm = 0, int units = 4, double scale = 1)
    {
        double s = scale;
        var entities = Rectangle(420 / s, 297 / s).ToList();
        double x0 = (90 + viewShiftMm) / s, x1 = (150 + viewShiftMm) / s, y0 = 160 / s, y1 = 200 / s;
        entities.Add(Line(x0, y0, x1, y0));
        entities.Add(Line(x1, y0, x1, y1));
        entities.Add(Line(x1, y1, x0, y1));
        entities.Add(Line(x0, y1, x0, y0));
        entities.Add(new object[] { 0, "CIRCLE", 8, "0", 10, 120.0 / s, 20, 180.0 / s, 30, 0.0, 40, 5.0 / s });
        entities.Add(Line(120 / s, 150 / s, 120 / s, 210 / s));
        entities.Add(new object[] { 0, "MTEXT", 8, "0", 10, 300.0 / s, 20, 30.0 / s, 1, "TITLE" });
        return Drawing(new object[] { 9, "$INSUNITS", 70, units }, entities.ToArray());
    }

    [Fact]
    public void A_dxf_lies_on_its_sheet_and_holds_the_largest_views_edges()
    {
        string path = Write("block.dxf", Sheet1Dxf());
        var small = new ViewExpectation { Name = "Drawing View2", Extents = new SheetBox { Min = new SheetPoint(300, 200), Max = new SheetPoint(310, 210) }, Scale = 1 };
        var file = DrawingFileCheck.Dxf(path, _started, Sheet1(Front(), small));
        Assert.Equal("DXF", file.Header);
        Assert.Equal("Drawing View1", file.View);
        Assert.Equal(90, file.ViewExtents!.Value.MinX, 9);
        Assert.Equal(200, file.ViewExtents.Value.MaxY, 9);
        Assert.Equal(420, file.Extents!.Value.MaxX, 9);
        Assert.Contains(file.Checks, c => c.Contains("lie on Sheet1 (420 × 297 mm)"));
        Assert.Contains(file.Checks, c => c.Contains("Drawing View1's drawn extents"));
    }

    [Fact]
    public void A_dxf_in_inches_is_read_in_millimetres()
    {
        string path = Write("block.dxf", Sheet1Dxf(units: 1, scale: 25.4));
        var file = DrawingFileCheck.Dxf(path, _started, Sheet1());
        Assert.Equal(150, file.ViewExtents!.Value.MaxX, 6);
        Assert.Contains(file.Checks, c => c.Contains("$INSUNITS 1"));
    }

    [Fact]
    public void A_dxf_without_the_largest_views_edges_where_it_is_drawn_is_refused()
    {
        string path = Write("block.dxf", Sheet1Dxf(viewShiftMm: 1));
        var e = Assert.Throws<SwAgentException>(() => DrawingFileCheck.Dxf(path, _started, Sheet1()));
        Assert.Equal("view_in_file", CheckOf(e));
        Assert.Contains("Drawing View1", e.Message);
    }

    [Fact]
    public void The_tolerance_grows_with_the_views_scale()
    {
        string path = Write("block.dxf", Sheet1Dxf(viewShiftMm: 0.015));
        Assert.Equal("view_in_file", CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Dxf(path, _started, Sheet1(Front(scale: 1))))));
        Assert.Equal("Drawing View1", DrawingFileCheck.Dxf(path, _started, Sheet1(Front(scale: 2))).View);
    }

    [Fact]
    public void An_entity_off_the_sheet_is_refused()
    {
        string text = Sheet1Dxf().Replace("0\r\nENDSEC\r\n0\r\nEOF", string.Join("", Line(400, 100, 430, 100).Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) + "\r\n")) + "0\r\nENDSEC\r\n0\r\nEOF");
        string path = Write("block.dxf", text);
        Assert.Equal("dxf_on_sheet", CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Dxf(path, _started, Sheet1()))));
    }

    [Fact]
    public void A_sheet_without_views_needs_only_its_entities_on_it()
    {
        string path = Write("details.dxf", Drawing(new object[] { 9, "$INSUNITS", 70, 4 }, Rectangle(210, 297)));
        var file = DrawingFileCheck.Dxf(path, _started, A4Portrait());
        Assert.Null(file.View);
        Assert.Contains(file.Checks, c => c.Contains("Details has no view"));
    }

    [Fact]
    public void A_dxf_with_no_entities_is_refused()
    {
        string path = Write("empty.dxf", Drawing(new object[0]));
        Assert.Equal("dxf_extents", CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Dxf(path, _started, Sheet1()))));
    }

    // ---- DWG ----

    [Fact]
    public void A_dwg_is_its_version_and_at_least_a_kilobyte()
    {
        var bytes = Encoding.ASCII.GetBytes("AC1032").Concat(new byte[2000]).ToArray();
        var file = DrawingFileCheck.Dwg(Write("block.dwg", bytes), _started);
        Assert.Equal("AC1032", file.Header);
    }

    [Theory]
    [InlineData("ABCDEF", 2000, "file_header")]
    [InlineData("AC1032", 100, "dwg_size")]
    public void A_dwg_that_is_not_one_is_refused(string head, int padding, string check)
    {
        var bytes = Encoding.ASCII.GetBytes(head).Concat(new byte[padding]).ToArray();
        Assert.Equal(check, CheckOf(Assert.Throws<SwAgentException>(() => DrawingFileCheck.Dwg(Write("block.dwg", bytes), _started))));
    }
}
