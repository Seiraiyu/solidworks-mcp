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
