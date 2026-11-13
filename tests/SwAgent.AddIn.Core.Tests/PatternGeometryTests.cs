using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PatternGeometryTests
{
    static Transform At(double x, double y, double z) => new Transform { Origin = new Point3(x, y, z) };

    static readonly List<(string, Transform)> Pin = new List<(string, Transform)> { ("pin-1", At(10, 0, 0)) };

    [Fact]
    public void A_linear_grid_moves_the_seed_by_spacing_times_i_and_j()
    {
        var readings = PatternGeometry.Linear(Pin, new Vector3(2, 0, 0), 20, 3, new Vector3(0, -1, 0), 15, 2);
        Assert.Equal(4, readings.Count);
        var along = readings[0];
        Assert.Equal("along/along", along.Sense);
        Assert.Equal(5, along.Instances.Count);
        Assert.Contains(along.Instances, e => e.At.Origin.X == 50 && e.At.Origin.Y == -15);
        Assert.All(along.Instances, e => Assert.Equal("pin-1", e.Seed));
    }

    [Theory]
    [InlineData(4, 360, true, 90)]
    [InlineData(3, 180, true, 90)]
    [InlineData(3, 30, false, 30)]
    public void The_circular_step_follows_equal_spacing(int count, double angle, bool equal, double step) =>
        Assert.Equal(step, PatternGeometry.CircularStep(count, angle, equal), 9);

    [Fact]
    public void A_circular_pattern_turns_origin_and_axes_about_the_axis()
    {
        var readings = PatternGeometry.Circular(new List<(string, Transform)> { ("pin-1", At(70, 40, 20)) }, new Point3(40, 40, 0), new Vector3(0, 0, 1), 4, 90);
        var first = readings[0].Instances[0];
        Assert.Equal(40, first.At.Origin.X, 9);
        Assert.Equal(70, first.At.Origin.Y, 9);
        Assert.Equal(20, first.At.Origin.Z, 9);
        Assert.Equal(0, first.At.XAxis.X, 9);
        Assert.Equal(1, first.At.XAxis.Y, 9);
        Assert.Equal("against", readings[1].Sense);
        Assert.Equal(40, readings[1].Instances[0].At.Origin.X, 9);
        Assert.Equal(10, readings[1].Instances[0].At.Origin.Y, 9);
    }

    [Fact]
    public void Instances_are_paired_with_the_reading_they_follow()
    {
        var readings = PatternGeometry.Linear(Pin, new Vector3(1, 0, 0), 20, 3, null, 0, 1);
        var actual = new List<(string, Transform)> { ("pin-3", At(-30, 0, 0)), ("pin-2", At(-10, 0, 0)) };
        var (reading, instances) = PatternGeometry.Match(readings, actual, checkAxes: true);
        Assert.Equal("against", reading.Sense);
        Assert.Equal(new[] { "pin-3", "pin-2" }, instances.Select(i => i.Name));
        Assert.All(instances, i => Assert.Equal(0, i.OffsetMm, 9));
        Assert.Equal(-30, instances[0].Expected.X, 9);
    }

    [Fact]
    public void An_instance_off_by_more_than_a_micron_is_verification_failed()
    {
        var readings = PatternGeometry.Linear(Pin, new Vector3(1, 0, 0), 20, 2, null, 0, 1);
        var e = Assert.Throws<SwAgentException>(() => PatternGeometry.Match(readings, new List<(string, Transform)> { ("pin-2", At(30.01, 0, 0)) }, true));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("instance_offset_mm", e.Error.Details!["check"]);
        Assert.Equal("pin-2", e.Error.Details["instance"]);
    }

    [Fact]
    public void A_wrong_count_is_verification_failed_before_any_pairing()
    {
        var readings = PatternGeometry.Linear(Pin, new Vector3(1, 0, 0), 20, 3, null, 0, 1);
        var e = Assert.Throws<SwAgentException>(() => PatternGeometry.Match(readings, new List<(string, Transform)> { ("pin-2", At(30, 0, 0)) }, true));
        Assert.Equal("instances", e.Error.Details!["check"]);
        Assert.Equal(2, e.Error.Details["expected"]);
    }

    [Fact]
    public void A_turned_instance_fails_when_axes_are_checked()
    {
        var readings = PatternGeometry.Linear(Pin, new Vector3(1, 0, 0), 20, 2, null, 0, 1);
        var turned = new Transform { Origin = new Point3(30, 0, 0), XAxis = new Vector3(0, 1, 0), YAxis = new Vector3(-1, 0, 0) };
        var e = Assert.Throws<SwAgentException>(() => PatternGeometry.Match(readings, new List<(string, Transform)> { ("pin-2", turned) }, true));
        Assert.Equal("instance_turn_deg", e.Error.Details!["check"]);
    }

    static EntityInfo Cylinder(double x, double y, double z, double r) =>
        new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Cylinder, Origin = new Point3(x, y, z), Axis = new Vector3(0, 0, 1), RadiusMm = r };

    [Fact]
    public void Holes_merge_coaxial_cylinders_and_skip_other_faces()
    {
        var faces = new[]
        {
            Cylinder(60, 40, 5, 5), Cylinder(60, 40, 9, 8), Cylinder(40, 60, 5, 5),
            new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Origin = new Point3(0, 0, 10), Normal = new Vector3(0, 0, 1) },
        };
        var holes = PatternGeometry.HoleAxes(faces);
        Assert.Equal(2, holes.Count);
    }

    static readonly IReadOnlyList<HoleAxis> FourHoles = PatternGeometry.HoleAxes(new[]
    {
        Cylinder(60, 40, 5, 5), Cylinder(40, 60, 5, 5), Cylinder(20, 40, 5, 5), Cylinder(40, 20, 5, 5),
    });

    [Fact]
    public void A_feature_pattern_puts_one_instance_on_every_other_hole()
    {
        var seeds = new List<(string, Transform)> { ("pin-1", At(60, 40, 0)) };
        var actual = new List<(string, Transform)> { ("pin-2", At(40, 60, 0)), ("pin-3", At(20, 40, 0)), ("pin-4", At(40, 20, 0)) };
        var instances = PatternGeometry.MatchHoles(seeds, FourHoles, actual);
        Assert.Equal(3, instances.Count);
        Assert.All(instances, i => Assert.Equal(0, i.OffsetMm, 9));
        Assert.All(instances, i => Assert.Null(i.TurnDeg));
    }

    [Fact]
    public void Two_instances_on_one_hole_are_refused()
    {
        var seeds = new List<(string, Transform)> { ("pin-1", At(60, 40, 0)) };
        var actual = new List<(string, Transform)> { ("pin-2", At(40, 60, 0)), ("pin-3", At(40, 60, 0)), ("pin-4", At(40, 20, 0)) };
        var e = Assert.Throws<SwAgentException>(() => PatternGeometry.MatchHoles(seeds, FourHoles, actual));
        Assert.Equal("instance_offset_mm", e.Error.Details!["check"]);
    }

    [Fact]
    public void An_instance_at_the_wrong_height_is_refused()
    {
        var seeds = new List<(string, Transform)> { ("pin-1", At(60, 40, 0)) };
        var actual = new List<(string, Transform)> { ("pin-2", At(40, 60, 2)), ("pin-3", At(20, 40, 0)), ("pin-4", At(40, 20, 0)) };
        var e = Assert.Throws<SwAgentException>(() => PatternGeometry.MatchHoles(seeds, FourHoles, actual));
        Assert.Equal("pin-2", e.Error.Details!["instance"]);
        Assert.Equal(2.0, (double)e.Error.Details["measured"]!, 9);
    }

    [Theory]
    [InlineData(GeometryType.Line, ReferenceKind.Edge, true)]
    [InlineData(GeometryType.Plane, ReferenceKind.Face, true)]
    [InlineData(GeometryType.Cylinder, ReferenceKind.Face, false)]
    public void Directions_come_from_edges_axes_and_planes(GeometryType geometry, ReferenceKind kind, bool has)
    {
        var e = new EntityInfo { Kind = kind, Geometry = geometry, Origin = new Point3(), Axis = new Vector3(1, 0, 0), Normal = new Vector3(0, 0, 1) };
        Assert.Equal(has, PatternGeometry.DirectionOf(e) != null);
    }

    [Fact]
    public void A_cylinder_gives_an_axis_and_a_plane_does_not()
    {
        Assert.NotNull(PatternGeometry.AxisOf(Cylinder(40, 40, 5, 5)));
        Assert.Null(PatternGeometry.AxisOf(new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Origin = new Point3(), Normal = new Vector3(0, 0, 1) }));
    }
}
