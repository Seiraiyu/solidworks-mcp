using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ModelMeasureTests : FeatureTestBase
{
    ModelMeasureResult Measure(string a, string b) => Run(new ModelMeasureOperation(Services), new ModelMeasureRequest { A = a, B = b });

    string Ref(FakeDocument doc, ReferenceKind kind, EntityInfo info) => Services.References.Describe(doc, doc.Add(kind, info)).Reference!;

    [Fact]
    public void Two_vertices_give_their_distance_points_and_centre_distance()
    {
        var doc = Part();
        var m = Measure(Ref(doc, ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(0, 0, 0) }),
            Ref(doc, ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(3, 4, 0) }));
        Assert.Equal(5, m.DistanceMm, 9);
        Assert.Equal(5, m.CenterDistanceMm!.Value, 9);
        Assert.Equal(3, m.PointB!.X, 9);
        Assert.Null(m.AngleDeg);
        Assert.Equal(ReferenceKind.Vertex, m.A.Kind);
        Assert.StartsWith("r1.vertex.", m.B.Reference);
    }

    [Fact]
    public void Planes_faces_and_cylinders_give_the_angle_between_their_directions()
    {
        var doc = Part();
        Assert.Equal(90, Measure("front", "top").AngleDeg!.Value, 9);
        string top = Ref(doc, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, -1), Origin = new Point3(20, 10, 10) });
        Assert.Equal(0, Measure("front", top).AngleDeg!.Value, 9);
        string hole = Ref(doc, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Axis = new Vector3(0, 0, 1), Origin = new Point3(20, 10, 0), RadiusMm = 5 });
        var m = Measure(hole, "right");
        Assert.Equal(90, m.AngleDeg!.Value, 9);
        Assert.Null(m.CenterDistanceMm);
    }

    [Fact]
    public void A_sketch_or_a_solid_feature_is_not_measured()
    {
        var doc = Part();
        Block();
        Assert.Equal("a", Fails(() => Measure("Sketch1", "front")).Error.Details!["field"]);
        Assert.Equal("b", Fails(() => Measure("front", "Boss-Extrude1")).Error.Details!["field"]);
    }

    [Fact]
    public void A_measurement_that_changes_when_read_again_fails()
    {
        var doc = Part();
        doc.MeasureDrift = 0.5;
        var e = Fails(() => Measure("front", "top"));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("distance", e.Error.Details!["check"]);
    }
}
