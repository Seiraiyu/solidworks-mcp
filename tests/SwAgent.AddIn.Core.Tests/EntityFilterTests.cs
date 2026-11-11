using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class EntityFilterTests
{
    static EntityInfo Face(string feature, GeometryType geometry, Vector3? normal = null, Vector3? axis = null, double? distance = null) =>
        new EntityInfo { Kind = ReferenceKind.Face, Feature = feature, Geometry = geometry, Normal = normal, Axis = axis, DistanceMm = distance };

    static readonly EntityInfo[] Block =
    {
        Face("Boss-Extrude1", GeometryType.Plane, normal: new Vector3(0, 0, 1)),
        Face("Boss-Extrude1", GeometryType.Plane, normal: new Vector3(0, 0, -1)),
        Face("Boss-Extrude1", GeometryType.Plane, normal: new Vector3(1, 0, 0)),
        Face("Cut-Extrude1", GeometryType.Cylinder, axis: new Vector3(0, 0, -1)),
    };

    static string[] Kept(EntityQuery q, EntityInfo[]? from = null) =>
        EntityFilter.Apply(from ?? Block, e => e, q).Kept.Select(e => $"{e.Feature}/{e.Geometry}/{e.Normal ?? e.Axis}").ToArray();

    [Fact]
    public void No_filter_keeps_everything_in_order()
    {
        Assert.Equal(4, Kept(new EntityQuery()).Length);
    }

    [Fact]
    public void Feature_and_geometry_filter_ignoring_case_of_the_feature()
    {
        Assert.Equal(new[] { "Cut-Extrude1/Cylinder/(0, 0, -1)" }, Kept(new EntityQuery { Feature = "cut-extrude1" }));
        Assert.Equal(3, Kept(new EntityQuery { Geometry = GeometryType.Plane }).Length);
    }

    [Fact]
    public void An_edge_belongs_to_every_feature_of_the_faces_it_bounds()
    {
        var holeEdge = new EntityInfo
        {
            Kind = ReferenceKind.Edge, Geometry = GeometryType.Circle, Feature = "Boss-Extrude1",
            Features = new System.Collections.Generic.List<string> { "Boss-Extrude1", "Cut-Extrude1" },
        };
        Assert.True(EntityFilter.Matches(holeEdge, new EntityQuery { Feature = "cut-extrude1" }));
        Assert.True(EntityFilter.Matches(holeEdge, new EntityQuery { Feature = "Boss-Extrude1" }));
        Assert.False(EntityFilter.Matches(holeEdge, new EntityQuery { Feature = "Fillet1" }));
    }

    [Fact]
    public void A_direction_matches_a_normal_one_way_and_an_axis_either_way()
    {
        Assert.Equal(new[] { "Boss-Extrude1/Plane/(0, 0, 1)", "Cut-Extrude1/Cylinder/(0, 0, -1)" },
            Kept(new EntityQuery { Direction = new Vector3(0, 0, 1) }));
        Assert.Equal(new[] { "Boss-Extrude1/Plane/(1, 0, 0)" }, Kept(new EntityQuery { Direction = new Vector3(0.9999, 0.01, 0) }));
        Assert.Empty(Kept(new EntityQuery { Direction = new Vector3(0.95, 0.31, 0) }));
        Assert.Single(Kept(new EntityQuery { Direction = new Vector3(0.95, 0.31, 0), AngleToleranceDeg = 20 }));
    }

    [Fact]
    public void Near_keeps_within_the_distance_nearest_first()
    {
        var faces = new[]
        {
            Face("A", GeometryType.Plane, distance: 0.5),
            Face("B", GeometryType.Plane, distance: 0.0),
            Face("C", GeometryType.Plane, distance: 3.0),
            Face("D", GeometryType.Plane),
        };
        var q = new EntityQuery { Near = new Point3(0, 0, 0) };
        Assert.Equal(new[] { "B", "A" }, EntityFilter.Apply(faces, e => e, q).Kept.Select(e => e.Feature).ToArray());
        q.MaxDistanceMm = 5;
        Assert.Equal(new[] { "B", "A", "C" }, EntityFilter.Apply(faces, e => e, q).Kept.Select(e => e.Feature).ToArray());
    }

    [Fact]
    public void Limit_caps_what_is_kept_but_not_the_total()
    {
        var (kept, total) = EntityFilter.Apply(Block, e => e, new EntityQuery { Limit = 2 });
        Assert.Equal(2, kept.Count);
        Assert.Equal(4, total);
    }

    [Fact]
    public void Descriptions_say_what_the_entity_was()
    {
        Assert.Equal("plane face of Boss-Extrude1, 800 mm²",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 800 }));
        Assert.Equal("circle edge of Cut-Extrude1, 15.708 mm, radius 2.5 mm",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Edge, Geometry = GeometryType.Circle, Feature = "Cut-Extrude1", LengthMm = 15.70796, RadiusMm = 2.5 }));
        Assert.Equal("plane Front Plane",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Plane, Geometry = GeometryType.Plane, Name = "Front Plane" }));
        Assert.Equal("point vertex of Boss-Extrude1 at (40, 20, 10)",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Vertex, Geometry = GeometryType.Point, Feature = "Boss-Extrude1", Origin = new Point3(40, 20, 10) }));
    }

    [Fact]
    public void An_entity_of_a_component_names_the_component()
    {
        Assert.Equal("plane face of Boss-Extrude1 in sub-1/pin-1, 100 mm²",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 100, Component = "sub-1/pin-1" }));
        Assert.Equal("plane Front Plane in bracket-1",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Plane, Geometry = GeometryType.Plane, Name = "Front Plane", Component = "bracket-1" }));
        Assert.Equal("component bracket-1",
            EntityFilter.Describe(new EntityInfo { Kind = ReferenceKind.Component, Geometry = GeometryType.Other, Name = "bracket-1", Component = "bracket-1" }));
    }
}
