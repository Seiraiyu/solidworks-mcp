using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ModelFindTests : DocumentTestBase
{
    readonly FakeDocument _part;

    public ModelFindTests()
    {
        _part = Sw.Track(FakeDocument.Part("Part1"));
        _part.AddFeature("Boss-Extrude1", "Extrusion");
        _part.AddFeature("Cut-Extrude1", "Cut");
        _part.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1), AreaMm2 = 800, DistanceMm = 0 });
        _part.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, -1), AreaMm2 = 800, DistanceMm = 10 });
        _part.Add(ReferenceKind.Face, new EntityInfo { Feature = "Cut-Extrude1", Geometry = GeometryType.Cylinder, Axis = new Vector3(0, 0, 1), RadiusMm = 2.5, DistanceMm = 0.4 });
        _part.Add(ReferenceKind.Edge, new EntityInfo
        {
            Feature = "Boss-Extrude1", Features = new System.Collections.Generic.List<string> { "Boss-Extrude1", "Cut-Extrude1" },
            Geometry = GeometryType.Circle, Axis = new Vector3(0, 0, 1), RadiusMm = 2.5, LengthMm = 15.708,
        });
        _part.Add(ReferenceKind.Edge, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Line, Axis = new Vector3(1, 0, 0), LengthMm = 40 });
    }

    ModelFindResult Find(ModelFindRequest request) => Run(new ModelFindOperation(Services), request);

    [Fact]
    public void Finds_faces_with_references_that_resolve_back()
    {
        var result = Find(new ModelFindRequest { Kind = TopologyKind.Face });
        Assert.Equal(3, result.Total);
        Assert.False(result.Truncated);
        Assert.All(result.Entities, e => Assert.StartsWith("r1.face.", e.Reference));
        var (entity, _) = Services.References.Resolve(_part, result.Entities[2].Reference!, "f");
        Assert.Equal(GeometryType.Cylinder, ((FakeEntity)entity).Info.Geometry);
    }

    [Fact]
    public void The_top_face_of_a_feature()
    {
        var result = Find(new ModelFindRequest { Kind = TopologyKind.Face, Feature = "Boss-Extrude1", Direction = new Vector3(0, 0, 1) });
        var top = Assert.Single(result.Entities);
        Assert.Equal(800, top.AreaMm2);
        Assert.Equal(new Vector3(0, 0, 1).ToString(), top.Normal!.ToString());
    }

    [Fact]
    public void The_circular_edges_of_a_hole_by_feature_reference()
    {
        string feature = Run(new ModelTreeOperation(Services), new ModelTreeRequest()).Features.Single(f => f.Name == "Cut-Extrude1").Reference!;
        var result = Find(new ModelFindRequest { Kind = TopologyKind.Edge, Feature = feature, Geometry = GeometryType.Circle });
        Assert.Equal(2.5, Assert.Single(result.Entities).RadiusMm);
    }

    [Fact]
    public void Near_a_point_nearest_first_within_the_distance()
    {
        var result = Find(new ModelFindRequest { Kind = TopologyKind.Face, Near = new Point3(1, 2, 10) });
        Assert.Equal(new double?[] { 0, 0.4 }, result.Entities.Select(e => e.DistanceMm));
        Assert.All(_part.DistanceAsked, p => Assert.Equal("(1, 2, 10)", p.ToString()));
        Assert.Equal(3, Find(new ModelFindRequest { Kind = TopologyKind.Face, Near = new Point3(0, 0, 0), MaxDistanceMm = 20 }).Total);
    }

    [Fact]
    public void Limit_truncates_and_says_so()
    {
        var result = Find(new ModelFindRequest { Kind = TopologyKind.Face, Limit = 1 });
        Assert.Single(result.Entities);
        Assert.Equal(3, result.Total);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("angle_tolerance_deg")]
    [InlineData("max_distance_mm")]
    [InlineData("direction")]
    [InlineData("feature")]
    public void Bad_filters_are_invalid_argument_naming_the_field(string field)
    {
        var request = new ModelFindRequest { Kind = TopologyKind.Face };
        switch (field)
        {
            case "limit": request.Limit = 0; break;
            case "angle_tolerance_deg": request.AngleToleranceDeg = 5; break;
            case "max_distance_mm": request.MaxDistanceMm = 2; break;
            case "direction": request.Direction = new Vector3(0, 0, 0); break;
            case "feature": request.Feature = "Boss-Extrude9"; break;
        }
        var e = Fails(() => Find(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }

    [Fact]
    public void A_plane_is_not_a_feature_to_search_under()
    {
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Find(new ModelFindRequest { Kind = TopologyKind.Face, Feature = "front" })).Code);
    }
}
