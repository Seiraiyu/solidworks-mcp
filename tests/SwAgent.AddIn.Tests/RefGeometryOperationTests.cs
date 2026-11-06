using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class RefgeomPlaneTests : FeatureTestBase
{
    FeatureResult Plane(RefgeomPlaneRequest request) => Run(new RefgeomPlaneOperation(Services), request);

    string Vertex(FakeDocument doc, double x, double y, double z) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(x, y, z) })).Reference!;

    string LineEdge(FakeDocument doc, Point3 at, Vector3 along) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = at, Axis = along, LengthMm = 40 })).Reference!;

    [Fact]
    public void An_offset_plane_is_parallel_and_that_far_away()
    {
        var doc = Part();
        var made = Plane(new RefgeomPlaneRequest { Mode = PlaneMode.Offset, From = "front", DistanceMm = 15 });
        Assert.Equal("Plane1", made.Feature.Name);
        Assert.Equal(ReferenceKind.Plane, made.Feature.Kind);
        Assert.Empty(made.Faces);
        Assert.Contains("parallel to front, 15 mm from it", made.Checks);
        Assert.Equal(15, made.Feature.Origin!.Z, 9);
        Assert.Equal(15, doc.Planes.Single().DistanceMm);
        Assert.Equal("Ebene vorne", ((FakeEntity)doc.Planes.Single().References.Single()).Info.Name);
    }

    [Fact]
    public void A_flipped_offset_is_on_the_other_side()
    {
        var doc = Part();
        var made = Plane(new RefgeomPlaneRequest { Mode = PlaneMode.Offset, From = "front", DistanceMm = 15, Flip = true });
        Assert.Equal(-15, made.Feature.Origin!.Z, 9);
        Assert.True(doc.Planes.Single().Flip);
    }

    [Fact]
    public void An_angle_plane_turns_about_its_axis()
    {
        var doc = Part();
        var made = Plane(new RefgeomPlaneRequest { Mode = PlaneMode.Angle, From = "front", Axis = LineEdge(doc, new Point3(20, 0, 0), new Vector3(1, 0, 0)), AngleDeg = 30 });
        Assert.Contains(made.Checks, c => c.StartsWith("at 30° to front"));
        Assert.Equal(2, doc.Planes.Single().References.Count);
    }

    [Fact]
    public void Three_points_parallel_through_a_point_and_normal_to_a_line_are_where_they_should_be()
    {
        var doc = Part();
        Plane(new RefgeomPlaneRequest { Mode = PlaneMode.ThreePoints, Points = new List<string> { Vertex(doc, 0, 0, 0), Vertex(doc, 40, 0, 0), Vertex(doc, 0, 20, 10) } });
        Plane(new RefgeomPlaneRequest { Mode = PlaneMode.ParallelThroughPoint, From = "top", Through = Vertex(doc, 5, 7, 9) });
        var normal = Plane(new RefgeomPlaneRequest { Mode = PlaneMode.NormalToCurve, Curve = LineEdge(doc, new Point3(0, 0, 5), new Vector3(0, 0, 1)), Through = Vertex(doc, 0, 0, 5) });
        Assert.Contains("normal to the line, through the point", normal.Checks);
        Assert.Equal(new[] { PlaneMode.ThreePoints, PlaneMode.ParallelThroughPoint, PlaneMode.NormalToCurve }, doc.Planes.Select(p => p.Mode));
    }

    [Theory]
    [InlineData("axis_on_offset", "axis")]
    [InlineData("no_from", "from")]
    [InlineData("no_distance", "distance_mm")]
    [InlineData("angle_180", "angle_deg")]
    [InlineData("two_points", "points")]
    [InlineData("cylinder_from", "from")]
    [InlineData("round_axis", "axis")]
    public void A_wrong_request_is_refused_by_its_field(string wrong, string field)
    {
        var doc = Part();
        var request = new RefgeomPlaneRequest { Mode = PlaneMode.Offset, From = "front", DistanceMm = 15 };
        switch (wrong)
        {
            case "axis_on_offset": request.Axis = "front"; break;
            case "no_from": request.From = null; break;
            case "no_distance": request.DistanceMm = null; break;
            case "angle_180": request = new RefgeomPlaneRequest { Mode = PlaneMode.Angle, From = "front", Axis = LineEdge(doc, new Point3(0, 0, 0), new Vector3(1, 0, 0)), AngleDeg = 180 }; break;
            case "two_points": request = new RefgeomPlaneRequest { Mode = PlaneMode.ThreePoints, Points = new List<string> { Vertex(doc, 0, 0, 0), Vertex(doc, 1, 0, 0) } }; break;
            case "cylinder_from":
                request.From = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder })).Reference;
                break;
            case "round_axis":
                var round = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle });
                request = new RefgeomPlaneRequest { Mode = PlaneMode.Angle, From = "front", Axis = Services.References.Describe(doc, round).Reference, AngleDeg = 30 };
                break;
        }
        Assert.Equal(field, Fails(() => Plane(request)).Error.Details!["field"]);
        Assert.Empty(doc.Planes);
    }

    [Fact]
    public void A_plane_off_where_it_should_be_is_deleted()
    {
        var doc = Part();
        doc.RefGeometryError = 0.5;
        var e = Fails(() => Plane(new RefgeomPlaneRequest { Mode = PlaneMode.Offset, From = "front", DistanceMm = 15 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("plane_offset", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Plane1");
    }
}

public class RefgeomAxisAndPointTests : FeatureTestBase
{
    FeatureResult Axis(RefgeomAxisRequest request) => Run(new RefgeomAxisOperation(Services), request);

    FeatureResult Point(RefgeomPointRequest request) => Run(new RefgeomPointOperation(Services), request);

    string Ref(FakeDocument doc, ReferenceKind kind, EntityInfo info) => Services.References.Describe(doc, doc.Add(kind, info)).Reference!;

    [Fact]
    public void Two_planes_meet_in_an_axis_along_their_line()
    {
        var doc = Part();
        var made = Axis(new RefgeomAxisRequest { Mode = AxisMode.TwoPlanes, Entities = new List<string> { "front", "right" } });
        Assert.Equal("Axis1", made.Feature.Name);
        Assert.Equal(ReferenceKind.Axis, made.Feature.Kind);
        Assert.Contains("along the line where the two planes meet", made.Checks);
        Assert.Equal(2, doc.Axes.Single().References.Count);
    }

    [Fact]
    public void Two_points_a_cylinder_and_an_edge_give_their_lines()
    {
        var doc = Part();
        Axis(new RefgeomAxisRequest
        {
            Mode = AxisMode.TwoPoints,
            Entities = new List<string>
            {
                Ref(doc, ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(0, 0, 0) }),
                Ref(doc, ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(40, 20, 10) }),
            },
        });
        Axis(new RefgeomAxisRequest { Mode = AxisMode.Cylinder, Entities = new List<string> { Ref(doc, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Axis = new Vector3(0, 0, 1), Origin = new Point3(20, 10, 0), RadiusMm = 5 }) } });
        var edge = Axis(new RefgeomAxisRequest { Mode = AxisMode.Edge, Entities = new List<string> { Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Axis = new Vector3(1, 0, 0), Origin = new Point3(20, 0, 0) }) } });
        Assert.Contains("along the edge", edge.Checks);
        Assert.Equal(3, doc.Axes.Count);
    }

    [Theory]
    [InlineData("one_plane", "entities")]
    [InlineData("flat_cylinder", "/entities/0")]
    [InlineData("round_edge", "/entities/0")]
    public void An_axis_on_the_wrong_entities_is_refused(string wrong, string field)
    {
        var doc = Part();
        var request = wrong switch
        {
            "one_plane" => new RefgeomAxisRequest { Mode = AxisMode.TwoPlanes, Entities = new List<string> { "front" } },
            "flat_cylinder" => new RefgeomAxisRequest { Mode = AxisMode.Cylinder, Entities = new List<string> { Ref(doc, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane }) } },
            _ => new RefgeomAxisRequest { Mode = AxisMode.Edge, Entities = new List<string> { Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle }) } },
        };
        Assert.Equal(field, Fails(() => Axis(request)).Error.Details!["field"]);
        Assert.Empty(doc.Axes);
    }

    [Fact]
    public void An_axis_off_its_line_is_deleted()
    {
        var doc = Part();
        doc.RefGeometryError = 0.25;
        Assert.Equal("axis_position", Fails(() => Axis(new RefgeomAxisRequest { Mode = AxisMode.TwoPlanes, Entities = new List<string> { "front", "right" } })).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Axis1");
    }

    [Fact]
    public void A_point_at_a_circles_centre_on_a_face_and_on_edges()
    {
        var doc = Part();
        string circle = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 10, 10), RadiusMm = 5 });
        var centre = Point(new RefgeomPointRequest { Mode = PointMode.ArcCenter, Entities = new List<string> { circle } });
        Assert.Equal("Point1", centre.Feature.Name);
        Assert.Equal(ReferenceKind.Point, centre.Feature.Kind);
        Assert.Contains("at the circle's centre (20, 10, 10)", centre.Checks);
        var top = Point(new RefgeomPointRequest { Mode = PointMode.FaceCenter, Entities = new List<string> { Ref(doc, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1), Origin = new Point3(20, 10, 10) }) } });
        Assert.Contains("on the face's plane", top.Checks);
        string onEdge = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(20, 0, 0), DistanceMm = 0 });
        Point(new RefgeomPointRequest { Mode = PointMode.AlongCurve, Entities = new List<string> { onEdge }, DistanceMm = 5 });
        Assert.Equal(5, doc.Points.Last().DistanceMm);
    }

    [Theory]
    [InlineData("distance_on_centre", "distance_mm")]
    [InlineData("no_distance", "distance_mm")]
    [InlineData("straight_arc", "/entities/0")]
    [InlineData("one_edge_intersection", "entities")]
    public void A_point_on_the_wrong_entities_is_refused(string wrong, string field)
    {
        var doc = Part();
        string circle = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(0, 0, 0) });
        string line = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 0, 0) });
        var request = wrong switch
        {
            "distance_on_centre" => new RefgeomPointRequest { Mode = PointMode.ArcCenter, Entities = new List<string> { circle }, DistanceMm = 3 },
            "no_distance" => new RefgeomPointRequest { Mode = PointMode.AlongCurve, Entities = new List<string> { line } },
            "straight_arc" => new RefgeomPointRequest { Mode = PointMode.ArcCenter, Entities = new List<string> { line } },
            _ => new RefgeomPointRequest { Mode = PointMode.Intersection, Entities = new List<string> { line } },
        };
        Assert.Equal(field, Fails(() => Point(request)).Error.Details!["field"]);
        Assert.Empty(doc.Points);
    }

    [Fact]
    public void A_point_off_the_edges_it_should_be_on_is_deleted()
    {
        var doc = Part();
        var a = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 0, 0), DistanceMm = 0 });
        var b = Ref(doc, ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(0, 0, 0), DistanceMm = 0.1 });
        Assert.Equal("point_position", Fails(() => Point(new RefgeomPointRequest { Mode = PointMode.Intersection, Entities = new List<string> { a, b } })).Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Point1");
    }
}