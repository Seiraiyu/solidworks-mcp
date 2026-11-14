using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>The block (0..60, 0..40, 0..20) with its Ø10 hole at (20, 20), in a front view at 1:2 with its origin drawn at (100, 150).</summary>
public class AnnotationGeometryTests
{
    static readonly ViewTransform Front = Views.Front(0.5, 100, 150);

    static ViewEntityInfo Edge(Point3 from, Point3 to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z, length = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var mid = new Point3((from.X + to.X) / 2, (from.Y + to.Y) / 2, (from.Z + to.Z) / 2);
        return ViewGeometry.Draw(Front, new EntityInfo { Geometry = GeometryType.Line, Origin = mid, Axis = new Vector3(dx / length, dy / length, dz / length), LengthMm = length }, false);
    }

    static ViewEntityInfo Hole => ViewGeometry.Draw(Front, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, 1), RadiusMm = 5 }, false);

    static ViewEntityInfo Corner(Point3 p) => ViewGeometry.Draw(Front, new EntityInfo { Geometry = GeometryType.Point, Origin = p }, true);

    static ViewEntityInfo Left => Edge(new Point3(0, 0, 20), new Point3(0, 40, 20));

    static ViewEntityInfo Right => Edge(new Point3(60, 0, 20), new Point3(60, 40, 20));

    static ViewEntityInfo Bottom => Edge(new Point3(0, 0, 20), new Point3(60, 0, 20));

    static DimensionExpectation Expect(DimensionKind kind, params ViewEntityInfo[] drawn)
    {
        var e = AnnotationGeometry.Expect(kind, drawn, 0.5, out string why);
        Assert.True(e != null, why);
        return e!;
    }

    static string Refused(DimensionKind kind, params ViewEntityInfo[] drawn)
    {
        Assert.Null(AnnotationGeometry.Expect(kind, drawn, 0.5, out string why));
        return why;
    }

    [Fact]
    public void Horizontal_between_the_blocks_sides_is_60_whatever_the_scale()
    {
        var e = Expect(DimensionKind.Horizontal, Left, Right);
        Assert.Equal(60, e.Value, 9);
        Assert.Null(e.Alternative);
        Assert.Equal(60, Expect(DimensionKind.Horizontal, Bottom).Value, 9);
        Assert.Equal(40, Expect(DimensionKind.Vertical, Left).Value, 9);
    }

    [Fact]
    public void Horizontal_from_a_side_to_the_holes_centre_and_vertical_from_the_bottom()
    {
        Assert.Equal(20, Expect(DimensionKind.Horizontal, Left, Hole).Value, 9);
        Assert.Equal(20, Expect(DimensionKind.Vertical, Bottom, Hole).Value, 9);
        Assert.Equal(40, Expect(DimensionKind.Horizontal, Hole, Corner(new Point3(60, 40, 20))).Value, 9);
    }

    [Fact]
    public void A_horizontal_dimension_refuses_a_line_that_is_not_vertical_between_two_entities()
    {
        Assert.Contains("entity 0 is a line that is not", Refused(DimensionKind.Horizontal, Bottom, Hole));
        Assert.Contains("takes a straight edge", Refused(DimensionKind.Vertical, Hole));
    }

    [Fact]
    public void Linear_is_a_lines_length_the_distance_between_parallel_lines_or_from_a_line_to_a_point()
    {
        Assert.Equal(60, Expect(DimensionKind.Linear, Bottom).Value, 9);
        Assert.Equal(60, Expect(DimensionKind.Linear, Left, Right).Value, 9);
        Assert.Equal(20, Expect(DimensionKind.Linear, Bottom, Hole).Value, 9);
        Assert.Equal(System.Math.Sqrt(40 * 40 + 20 * 20), Expect(DimensionKind.Linear, Hole, Corner(new Point3(60, 40, 20))).Value, 9);
        Assert.Contains("not drawn parallel", Refused(DimensionKind.Linear, Left, Bottom));
        Assert.Contains("for a circle ask for radius or diameter", Refused(DimensionKind.Linear, Hole));
    }

    [Fact]
    public void Radius_and_diameter_are_the_holes_in_model_mm()
    {
        Assert.Equal(5, Expect(DimensionKind.Radius, Hole).Value, 9);
        Assert.Equal(10, Expect(DimensionKind.Diameter, Hole).Value, 9);
        Assert.Contains("one circular edge", Refused(DimensionKind.Diameter, Bottom));
    }

    [Fact]
    public void An_angle_is_the_one_between_the_lines_or_its_supplement()
    {
        var right = Expect(DimensionKind.Angle, Left, Bottom);
        Assert.Equal(90, right.Value, 9);
        Assert.Null(right.Alternative);
        var slant = Edge(new Point3(0, 0, 20), new Point3(40, 40, 20));
        var e = Expect(DimensionKind.Angle, slant, Bottom);
        Assert.Equal(45, e.Value, 9);
        Assert.Equal(135, e.Alternative!.Value, 9);
        Assert.True(AnnotationGeometry.Matches(e, 135.004, angle: true));
        Assert.False(AnnotationGeometry.Matches(e, 45.02, angle: true));
        Assert.Contains("parallel", Refused(DimensionKind.Angle, Left, Right));
    }

    [Fact]
    public void A_value_matches_within_a_micron()
    {
        var e = Expect(DimensionKind.Horizontal, Left, Right);
        Assert.True(AnnotationGeometry.Matches(e, 60.0009, angle: false));
        Assert.False(AnnotationGeometry.Matches(e, 59.998, angle: false));
    }

    [Fact]
    public void Entities_drawn_at_one_place_have_nothing_to_dimension()
    {
        Assert.Contains("nothing to dimension", Refused(DimensionKind.Horizontal, Corner(new Point3(0, 0, 20)), Corner(new Point3(0, 0, 0))));
    }

    [Fact]
    public void A_hole_seen_face_on_takes_one_centre_mark_for_both_its_edges()
    {
        var back = ViewGeometry.Draw(Front, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1), RadiusMm = 5 }, false);
        var kept = AnnotationGeometry.DistinctCentres(new[] { ("front", Hole), ("back", back), ("side", Bottom) }, x => x.Item2);
        Assert.Equal(new[] { "front" }, kept.Select(k => k.Item1));
    }

    [Fact]
    public void A_cylinder_seen_from_the_side_takes_one_centre_line_per_axis()
    {
        var top = Views.Top(1, 100, 100);
        var halves = new[] { ("half 1", new Point3(20, 20, 0), new Vector3(0, 0, 1)), ("half 2", new Point3(20, 20, 20), new Vector3(0, 0, -1)), ("other", new Point3(40, 20, 0), new Vector3(0, 0, 1)) };
        var kept = AnnotationGeometry.DistinctAxes(top, halves, h => (h.Item2, h.Item3));
        Assert.Equal(new[] { "half 1", "other" }, kept.Select(k => k.Item1));
        Assert.Empty(AnnotationGeometry.DistinctAxes(Front, halves, h => (h.Item2, h.Item3)));
        Assert.True(AnnotationGeometry.SeenFromSide(top, new Vector3(0, 0, 1)));
        Assert.False(AnnotationGeometry.SeenFromSide(Front, new Vector3(0, 0, 1)));
    }

    [Fact]
    public void A_centre_line_goes_between_two_parallel_lines_only()
    {
        Assert.True(AnnotationGeometry.Parallel(Left, Right));
        Assert.False(AnnotationGeometry.Parallel(Left, Bottom));
        Assert.False(AnnotationGeometry.Parallel(Left, Left));
        Assert.False(AnnotationGeometry.Parallel(Left, Hole));
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData(" B ", "B")]
    [InlineData("AB", "AB")]
    [InlineData("I", null)]
    [InlineData("Q", null)]
    [InlineData("A1", null)]
    [InlineData("ABCD", null)]
    [InlineData("", null)]
    public void Datum_labels_are_one_to_three_letters_without_I_O_and_Q(string given, string? label)
    {
        Assert.Equal(label, AnnotationGeometry.DatumLabel(given));
    }

    [Fact]
    public void Form_tolerances_take_no_datum()
    {
        Assert.Equal(4, System.Enum.GetValues(typeof(GtolCharacteristic)).Cast<GtolCharacteristic>().Count(AnnotationGeometry.IsForm));
        Assert.False(AnnotationGeometry.IsForm(GtolCharacteristic.Position));
    }

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(0.025, "0.025")]
    [InlineData(2, "2")]
    public void Tolerances_are_written_invariant(double mm, string text)
    {
        Assert.Equal(text, AnnotationGeometry.ToleranceText(mm));
        Assert.Equal(mm, AnnotationGeometry.ParseTolerance(text));
    }

    [Fact]
    public void A_tolerance_reads_through_a_symbol_code_and_text_without_a_number_reads_none()
    {
        Assert.Equal(0.1, AnnotationGeometry.ParseTolerance("<MOD-DIAM>0.1"));
        Assert.Null(AnnotationGeometry.ParseTolerance("<MOD-DIAM>"));
    }

    [Fact]
    public void Reference_dimensions_name_a_view_of_the_drawing_and_model_dimensions_do_not()
    {
        var owners = new List<string> { "Sheet1", "Drawing View1" };
        Assert.True(AnnotationGeometry.IsModelDimension("D1@Sketch2@block.Part", owners));
        Assert.False(AnnotationGeometry.IsModelDimension("RD1@Drawing View1", owners));
    }
}
