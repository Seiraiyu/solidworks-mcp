using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DxfFileTests
{
    /// <summary>An ASCII DXF of these group codes and values, one per line, CRLF as SOLIDWORKS writes it.</summary>
    internal static string Dxf(params object[] pairs)
    {
        var text = new StringBuilder();
        for (int i = 0; i < pairs.Length; i += 2)
        {
            text.Append(Convert.ToString(pairs[i], CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append(Convert.ToString(pairs[i + 1], CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>A whole DXF: a header with these variables (name, then its pairs) and these entities.</summary>
    internal static string Drawing(object[] header, params object[][] entities)
    {
        var pairs = new List<object> { 0, "SECTION", 2, "HEADER" };
        pairs.AddRange(header);
        pairs.AddRange(new object[] { 0, "ENDSEC", 0, "SECTION", 2, "ENTITIES" });
        foreach (var e in entities) pairs.AddRange(e);
        pairs.AddRange(new object[] { 0, "ENDSEC", 0, "EOF" });
        return Dxf(pairs.ToArray());
    }

    internal static object[] Line(double x1, double y1, double x2, double y2, string layer = "0", string? linetype = null)
    {
        var pairs = new List<object> { 0, "LINE", 8, layer };
        if (linetype != null) pairs.AddRange(new object[] { 6, linetype });
        pairs.AddRange(new object[] { 10, x1, 20, y1, 30, 0.0, 11, x2, 21, y2, 31, 0.0 });
        return pairs.ToArray();
    }

    /// <summary>The outline of a w × h rectangle from the origin as four lines on layer 0.</summary>
    internal static object[][] Rectangle(double w, double h) => new[]
    {
        Line(0, 0, w, 0), Line(w, 0, w, h), Line(w, h, 0, h), Line(0, h, 0, 0),
    };

    internal static object[] Extents(double minX, double minY, double maxX, double maxY) =>
        new object[] { 9, "$EXTMIN", 10, minX, 20, minY, 30, 0.0, 9, "$EXTMAX", 10, maxX, 20, maxY, 30, 0.0 };

    static void Box(DxfBox? box, double minX, double minY, double maxX, double maxY)
    {
        Assert.NotNull(box);
        var b = box!.Value;
        Assert.Equal(minX, b.MinX, 9);
        Assert.Equal(minY, b.MinY, 9);
        Assert.Equal(maxX, b.MaxX, 9);
        Assert.Equal(maxY, b.MaxY, 9);
    }

    [Fact]
    public void The_header_gives_the_drawing_extents_and_its_units()
    {
        var header = Extents(0, 0, 136.5664, 96.5664).Concat(new object[] { 9, "$INSUNITS", 70, 4 }).ToArray();
        var dxf = DxfFile.Parse(Drawing(header, Rectangle(136.5664, 96.5664)));
        Box(dxf.HeaderExtents, 0, 0, 136.5664, 96.5664);
        Box(dxf.EntityExtents, 0, 0, 136.5664, 96.5664);
        Assert.Equal(4, dxf.Units);
        Assert.Equal(4, dxf.Entities);
        Assert.Equal(4, dxf.Layers["0"]);
    }

    [Fact]
    public void Without_header_extents_the_entities_give_them()
    {
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), Rectangle(100, 60)));
        Assert.Null(dxf.HeaderExtents);
        Assert.Null(dxf.Units);
        Box(dxf.EntityExtents, 0, 0, 100, 60);
    }

    [Fact]
    public void Header_extents_that_were_never_set_count_as_absent()
    {
        // AutoCAD's "empty drawing" extents: min 1e20, max -1e20.
        var dxf = DxfFile.Parse(Drawing(Extents(1e20, 1e20, -1e20, -1e20), Rectangle(100, 60)));
        Assert.Null(dxf.HeaderExtents);
        Box(dxf.EntityExtents, 0, 0, 100, 60);
    }

    [Fact]
    public void An_arc_reaches_as_far_as_the_quadrants_it_sweeps()
    {
        // From 45° to 135° counter-clockwise it passes 90°, so it reaches y = r; it passes neither 0° nor 180°.
        var arc = new object[] { 0, "ARC", 8, "0", 10, 10.0, 20, 10.0, 30, 0.0, 40, 5.0, 50, 45.0, 51, 135.0 };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), arc));
        double c = 5 * Math.Cos(Math.PI / 4);
        Box(dxf.EntityExtents, 10 - c, 10 + c, 10 + c, 15);
    }

    [Fact]
    public void An_arc_across_zero_degrees_reaches_x_plus_r()
    {
        var arc = new object[] { 0, "ARC", 8, "0", 10, 0.0, 20, 0.0, 40, 2.0, 50, 300.0, 51, 60.0 };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), arc));
        Box(dxf.EntityExtents, 1, -Math.Sqrt(3), 2, Math.Sqrt(3));
    }

    [Fact]
    public void A_circle_and_a_polyline_with_a_bulge_are_measured_whole()
    {
        var circle = new object[] { 0, "CIRCLE", 8, "0", 10, 50.0, 20, 30.0, 40, 2.0 };
        // From (0, 0) to (20, 0) with bulge 1: a half circle of radius 10 below the chord (counter-clockwise from (0, 0)).
        var polyline = new object[] { 0, "LWPOLYLINE", 8, "0", 90, 2, 70, 0, 10, 0.0, 20, 0.0, 42, 1.0, 10, 20.0, 20, 0.0 };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), circle, polyline));
        Box(dxf.EntityExtents, 0, -10, 52, 32);
        Assert.Equal(2, dxf.Entities);
    }

    [Fact]
    public void A_closed_polyline_of_vertices_is_measured_and_its_vertices_are_not_entities()
    {
        var pairs = new object[]
        {
            0, "POLYLINE", 8, "0", 66, 1, 70, 1,
            0, "VERTEX", 8, "0", 10, 0.0, 20, 0.0,
            0, "VERTEX", 8, "0", 10, 30.0, 20, 0.0,
            0, "VERTEX", 8, "0", 10, 30.0, 20, 10.0, 42, 0.0,
            0, "SEQEND", 8, "0",
        };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), pairs));
        Box(dxf.EntityExtents, 0, 0, 30, 10);
        Assert.Equal(1, dxf.Entities);
        Assert.Equal(1, dxf.Layers["0"]);
    }

    [Fact]
    public void A_bend_line_is_a_line_across_the_outline_whatever_its_layer()
    {
        // The 6.D live run's tab-saved-sketches.dxf (ExportToDWG2): the bend line is on layer 0, linetype Continuous,
        // like the outline, and runs from one side of it to the other.
        var entities = new[]
        {
            Line(100, -58.1415926536, 0, -58.1415926536, "0", "Continuous"),
            Line(100, -83.2831853072, 0, -83.2831853072, "0", "Continuous"),
            Line(0, -83.2831853072, 0, 0, "0", "Continuous"),
            Line(0, 0, 100, 0, "0", "Continuous"),
            Line(100, 0, 100, -83.2831853072, "0", "Continuous"),
        };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), entities));
        Assert.Equal(1, dxf.BendLines);
        Assert.Equal(0, dxf.NamedBendLines);
        Assert.Equal(5, dxf.Entities);
    }

    [Fact]
    public void An_outline_alone_has_no_bend_lines()
    {
        // The 6.D live run's box.dxf (ExportFlatPatternView): CADapter's box flat, its 12 outline lines and nothing else.
        double[] xs = { 71.4168146928, 89.7, 189.7, 207.9831853072 }, ys = { 59.6668146928, 82.95, 132.95, 156.2331853072 };
        var outline = new[]
        {
            Line(xs[2], ys[0], xs[1], ys[0]), Line(xs[1], ys[1], xs[1], ys[0]), Line(xs[0], ys[1], xs[1], ys[1]),
            Line(xs[0], ys[1], xs[0], ys[2]), Line(xs[1], ys[2], xs[0], ys[2]), Line(xs[1], ys[3], xs[1], ys[2]),
            Line(xs[1], ys[3], xs[2], ys[3]), Line(xs[2], ys[2], xs[2], ys[3]), Line(xs[3], ys[2], xs[2], ys[2]),
            Line(xs[3], ys[2], xs[3], ys[1]), Line(xs[2], ys[1], xs[3], ys[1]), Line(xs[2], ys[0], xs[2], ys[1]),
        };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), outline));
        Assert.Equal(0, dxf.BendLines);
        Assert.Equal(12, dxf.Entities);
    }

    [Fact]
    public void A_bend_line_may_end_where_the_outline_is_split_into_two_lines_in_line()
    {
        var entities = new[]
        {
            Line(0, 0, 100, 0), Line(100, 0, 100, 30), Line(100, 30, 100, 60), Line(100, 60, 0, 60),
            Line(0, 60, 0, 30), Line(0, 30, 0, 0),
            Line(0, 30, 100, 30),
        };
        Assert.Equal(1, DxfFile.Parse(Drawing(Array.Empty<object>(), entities)).BendLines);
    }

    [Fact]
    public void A_bend_line_cut_by_a_hole_counts_each_piece()
    {
        var hole = new object[] { 0, "CIRCLE", 8, "0", 10, 50.0, 20, 30.0, 40, 2.0 };
        var entities = Rectangle(100, 60).Concat(new[] { Line(0, 30, 48, 30), Line(52, 30, 100, 30), hole }).ToArray();
        Assert.Equal(2, DxfFile.Parse(Drawing(Array.Empty<object>(), entities)).BendLines);
    }

    [Fact]
    public void Lines_that_do_not_cross_the_outline_are_not_bend_lines()
    {
        var entities = Rectangle(100, 60).Concat(new[]
        {
            Line(20, 20, 40, 20),        // a sketch line inside, touching nothing
            Line(0, 10, 30, 10),         // from the outline to nowhere
            Line(10, 0, 40, 0),          // along an edge of the outline
        }).ToArray();
        Assert.Equal(0, DxfFile.Parse(Drawing(Array.Empty<object>(), entities)).BendLines);
    }

    [Fact]
    public void Entities_on_a_bend_layer_or_linetype_are_counted_by_name_too()
    {
        var entities = Rectangle(100, 83.2832).Concat(new[]
        {
            Line(0, 58.1416, 100, 58.1416, "BENDLINES"),
            Line(0, 55, 100, 55, "0", "SW_BEND_DOWN"),
        }).ToArray();
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), entities));
        Assert.Equal(2, dxf.NamedBendLines);
        Assert.Equal(2, dxf.BendLines);
        Assert.Equal(6, dxf.Entities);
        Assert.Equal(1, dxf.Layers["BENDLINES"]);
        Assert.Equal(1, dxf.Linetypes["SW_BEND_DOWN"]);
    }

    [Fact]
    public void Text_and_inserts_are_counted_but_not_measured()
    {
        var text = new object[] { 0, "TEXT", 8, "0", 10, 500.0, 20, 500.0, 40, 3.0, 1, "UP 90° R 3" };
        var insert = new object[] { 0, "INSERT", 8, "0", 2, "BLOCK1", 10, -500.0, 20, -500.0 };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), Rectangle(10, 10).Concat(new[] { text, insert }).ToArray()));
        Box(dxf.EntityExtents, 0, 0, 10, 10);
        Assert.Equal(1, dxf.Skipped["TEXT"]);
        Assert.Equal(1, dxf.Skipped["INSERT"]);
    }

    [Fact]
    public void A_spline_is_bounded_by_its_control_points()
    {
        var spline = new object[] { 0, "SPLINE", 8, "0", 70, 8, 71, 3, 10, 0.0, 20, 0.0, 10, 5.0, 20, 8.0, 10, 10.0, 20, 0.0 };
        var dxf = DxfFile.Parse(Drawing(Array.Empty<object>(), spline));
        Box(dxf.EntityExtents, 0, 0, 10, 8);
        Assert.Equal(1, dxf.Splines);
    }

    [Theory]
    [InlineData(1, 25.4)]
    [InlineData(2, 304.8)]
    [InlineData(4, 1.0)]
    [InlineData(5, 10.0)]
    [InlineData(6, 1000.0)]
    public void Known_units_convert_to_millimetres(int units, double mm)
    {
        Assert.True(DxfFile.TryMmPer(units, out double got));
        Assert.Equal(mm, got, 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(13)]
    public void Unknown_units_are_not_guessed(int units)
    {
        Assert.False(DxfFile.TryMmPer(units, out _));
    }

    [Fact]
    public void A_binary_DXF_is_refused_by_name()
    {
        var e = Assert.Throws<FormatException>(() => DxfFile.Parse("AutoCAD Binary DXF\r\n\u001a\0..."));
        Assert.Contains("binary DXF", e.Message);
    }

    [Fact]
    public void Codes_and_values_that_do_not_pair_up_are_refused()
    {
        Assert.Contains("pair", Assert.Throws<FormatException>(() => DxfFile.Parse("0\r\nSECTION\r\n2\r\n")).Message);
    }

    [Fact]
    public void A_group_code_that_is_not_a_number_is_refused_with_its_line()
    {
        var e = Assert.Throws<FormatException>(() => DxfFile.Parse("0\r\nSECTION\r\nx\r\nHEADER\r\n"));
        Assert.Contains("line 3", e.Message);
    }

    [Fact]
    public void A_number_that_does_not_parse_is_refused()
    {
        var bad = new object[] { 0, "LINE", 8, "0", 10, "zero", 20, 0.0, 11, 1.0, 21, 1.0 };
        Assert.Contains("10", Assert.Throws<FormatException>(() => DxfFile.Parse(Drawing(Array.Empty<object>(), bad))).Message);
    }

    [Fact]
    public void A_file_with_no_entities_section_is_refused()
    {
        Assert.Contains("ENTITIES", Assert.Throws<FormatException>(() => DxfFile.Parse(Dxf(0, "SECTION", 2, "HEADER", 0, "ENDSEC", 0, "EOF"))).Message);
    }
}
