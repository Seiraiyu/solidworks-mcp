using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class MateGeometryTests
{
    static EntityInfo Plane(Point3 o, Vector3 n) => new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Origin = o, Normal = n };
    static EntityInfo Cylinder(Point3 o, Vector3 axis) => new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Cylinder, Origin = o, Axis = axis, RadiusMm = 5 };
    static EntityInfo Line(Point3 o, Vector3 axis) => new EntityInfo { Kind = ReferenceKind.Edge, Geometry = GeometryType.Line, Origin = o, Axis = axis };
    static EntityInfo Vertex(Point3 o) => new EntityInfo { Kind = ReferenceKind.Vertex, Geometry = GeometryType.Point, Origin = o };
    static EntityInfo Part(string name) => new EntityInfo { Kind = ReferenceKind.Component, Name = name, Geometry = GeometryType.Other };
    static double Never() => throw new System.InvalidOperationException("closest was not needed");

    [Theory]
    [InlineData(MateType.Coincident, MateShape.Planar, MateShape.Point, true)]
    [InlineData(MateType.Coincident, MateShape.Cylindrical, MateShape.Planar, false)]
    [InlineData(MateType.Concentric, MateShape.Cylindrical, MateShape.Circular, true)]
    [InlineData(MateType.Concentric, MateShape.Planar, MateShape.Cylindrical, false)]
    [InlineData(MateType.Tangent, MateShape.Planar, MateShape.Cylindrical, true)]
    [InlineData(MateType.Tangent, MateShape.Planar, MateShape.Planar, false)]
    [InlineData(MateType.Distance, MateShape.Planar, MateShape.Point, true)]
    [InlineData(MateType.Distance, MateShape.Cylindrical, MateShape.Planar, false)]
    [InlineData(MateType.Angle, MateShape.Linear, MateShape.Planar, true)]
    [InlineData(MateType.Lock, MateShape.Component, MateShape.Component, true)]
    [InlineData(MateType.Lock, MateShape.Planar, MateShape.Component, false)]
    public void Each_type_accepts_its_pairs(MateType type, MateShape a, MateShape b, bool accepted)
    {
        Assert.Equal(accepted, MateGeometry.Refusal(type, a, b) == null);
        Assert.Equal(accepted, MateGeometry.Refusal(type, b, a) == null);
    }

    [Fact]
    public void A_refusal_says_what_the_type_joins()
    {
        string why = MateGeometry.Refusal(MateType.Concentric, MateShape.Planar, MateShape.Planar)!;
        Assert.Contains("concentric", why);
        Assert.Contains("cylinder", why);
    }

    [Fact]
    public void Coincident_planes_are_measured_by_their_offset_not_by_overlap()
    {
        var m = MateGeometry.Measure(MateType.Coincident, Plane(new Point3(0, 0, 10), new Vector3(0, 0, 1)), Plane(new Point3(500, 500, 10), new Vector3(0, 0, -1)), null, null, Never).ToList();
        Assert.Equal(0, m.Single(x => x.Condition == "distance_mm").Measured, 9);
        Assert.Equal(0, m.Single(x => x.Condition == "angle_deg").Measured, 9);
        Assert.Null(MateGeometry.Failing(m));
    }

    [Fact]
    public void A_distance_mate_off_by_two_tenths_fails_and_says_so()
    {
        var m = MateGeometry.Measure(MateType.Distance, Plane(new Point3(0, 0, 0), new Vector3(-1, 0, 0)), Plane(new Point3(15.2, 40, 5), new Vector3(-1, 0, 0)), 15, null, Never).ToList();
        var failing = MateGeometry.Failing(m)!;
        Assert.Equal("distance_mm", failing.Condition);
        Assert.Equal("distance 15.2 mm, expected 15", MateGeometry.Say(failing));
    }

    [Fact]
    public void Concentric_measures_the_angle_and_offset_of_the_axes()
    {
        var hole = Cylinder(new Point3(20, 10, 5), new Vector3(0, 0, 1));
        var on = MateGeometry.Measure(MateType.Concentric, hole, Cylinder(new Point3(20, 10, 25), new Vector3(0, 0, -1)), null, null, Never).ToList();
        Assert.Null(MateGeometry.Failing(on));
        var off = MateGeometry.Measure(MateType.Concentric, hole, Cylinder(new Point3(20, 12, 25), new Vector3(0, 0, 1)), null, null, Never).ToList();
        Assert.Equal(2, off.Single(x => x.Condition == "axis_offset_mm").Measured, 9);
    }

    [Fact]
    public void Parallel_and_perpendicular_expect_90_between_a_plane_and_a_line()
    {
        var plane = Plane(new Point3(0, 0, 0), new Vector3(0, 0, 1));
        var flatLine = Line(new Point3(0, 0, 3), new Vector3(1, 0, 0));
        Assert.Null(MateGeometry.Failing(MateGeometry.Measure(MateType.Parallel, plane, flatLine, null, null, Never)));
        Assert.NotNull(MateGeometry.Failing(MateGeometry.Measure(MateType.Perpendicular, plane, flatLine, null, null, Never)));
        Assert.Null(MateGeometry.Failing(MateGeometry.Measure(MateType.Perpendicular, plane, Plane(new Point3(0, 0, 0), new Vector3(0, -1, 0)), null, null, Never)));
    }

    [Fact]
    public void Tangent_is_the_closest_distance()
    {
        var m = MateGeometry.Measure(MateType.Tangent, Plane(new Point3(0, 0, 10), new Vector3(0, 0, 1)), Cylinder(new Point3(0, 0, 15), new Vector3(1, 0, 0)), null, null, () => 0.0004).Single();
        Assert.Equal(0.0004, m.Measured, 9);
        Assert.Null(MateGeometry.Failing(new[] { m }));
    }

    [Fact]
    public void An_angle_mate_holds_at_its_value_or_its_supplement()
    {
        var top = Plane(new Point3(0, 0, 10), new Vector3(0, 0, 1));
        var turned = Plane(new Point3(0, 0, 0), new Vector3(0, -0.5, 0.8660254037844386));
        Assert.Null(MateGeometry.Failing(MateGeometry.Measure(MateType.Angle, top, turned, null, 30, Never)));
        Assert.Null(MateGeometry.Failing(MateGeometry.Measure(MateType.Angle, top, turned, null, 150, Never)));
        Assert.NotNull(MateGeometry.Failing(MateGeometry.Measure(MateType.Angle, top, turned, null, 45, Never)));
    }

    [Fact]
    public void A_point_on_a_plane_is_its_offset()
    {
        var m = MateGeometry.Measure(MateType.Coincident, Vertex(new Point3(7, 7, 10.0005)), Plane(new Point3(0, 0, 10), new Vector3(0, 0, 1)), null, null, Never).ToList();
        Assert.Single(m);
        Assert.Null(MateGeometry.Failing(m));
    }

    [Fact]
    public void A_locked_pair_that_moved_together_holds_and_one_that_turned_does_not()
    {
        var a0 = new Transform { Origin = new Point3(0, 0, 0) };
        var b0 = new Transform { Origin = new Point3(60, 0, 0) };
        var a1 = new Transform { Origin = new Point3(10, 5, 0) };
        var b1 = new Transform { Origin = new Point3(70, 5, 0) };
        Assert.Null(MateGeometry.Failing(MateGeometry.Locked(a0, b0, a1, b1)));
        var turned = new Transform { Origin = new Point3(70, 5, 0), XAxis = new Vector3(0, 1, 0), YAxis = new Vector3(-1, 0, 0), ZAxis = new Vector3(0, 0, 1) };
        Assert.Equal("relative_turn_deg", MateGeometry.Failing(MateGeometry.Locked(a0, b0, a1, turned))!.Condition);
    }

    [Fact]
    public void Shapes_come_from_kind_and_geometry()
    {
        Assert.Equal(MateShape.Planar, MateGeometry.ShapeOf(new EntityInfo { Kind = ReferenceKind.Plane, Geometry = GeometryType.Plane }));
        Assert.Equal(MateShape.Linear, MateGeometry.ShapeOf(new EntityInfo { Kind = ReferenceKind.Axis, Geometry = GeometryType.Line }));
        Assert.Equal(MateShape.Point, MateGeometry.ShapeOf(new EntityInfo { Kind = ReferenceKind.Point, Geometry = GeometryType.Point }));
        Assert.Equal(MateShape.Circular, MateGeometry.ShapeOf(new EntityInfo { Kind = ReferenceKind.Edge, Geometry = GeometryType.Circle }));
        Assert.Equal(MateShape.Component, MateGeometry.ShapeOf(Part("pin-1")));
    }
}
