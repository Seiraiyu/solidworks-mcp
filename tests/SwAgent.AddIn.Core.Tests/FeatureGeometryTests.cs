using System;
using System.Collections.Generic;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class FeatureGeometryTests
{
    static Point2 P(double x, double y) => new Point2(x, y);

    static List<(Point2, Point2)> Rectangle(double x0, double y0, double x1, double y1) => new List<(Point2, Point2)>
    {
        (P(x0, y0), P(x1, y0)),
        (P(x1, y0), P(x1, y1)),
        (P(x1, y1), P(x0, y1)),
        (P(x0, y1), P(x0, y0)),
    };

    static readonly List<(Point2, double)> NoCircles = new List<(Point2, double)>();

    [Fact]
    public void A_rectangle_is_its_area_about_its_centre()
    {
        var region = Profiles.Region(Rectangle(0, 0, 40, 20), NoCircles)!;
        Assert.Equal(800, region.AreaMm2, 9);
        Assert.Equal(20, region.Centroid.X, 9);
        Assert.Equal(10, region.Centroid.Y, 9);
    }

    [Fact]
    public void Lines_in_any_order_and_direction_still_close()
    {
        var lines = Rectangle(0, 0, 40, 20);
        lines.Reverse();
        lines[1] = (lines[1].Item2, lines[1].Item1);
        Assert.Equal(800, Profiles.Region(lines, NoCircles)!.AreaMm2, 9);
    }

    [Fact]
    public void A_circle_inside_a_rectangle_is_a_hole()
    {
        var region = Profiles.Region(Rectangle(0, 0, 40, 20), new List<(Point2, double)> { (P(30, 10), 5) })!;
        Assert.Equal(800 - Math.PI * 25, region.AreaMm2, 9);
        Assert.True(region.Centroid.X < 20, "the hole moves the centroid away from it");
    }

    [Fact]
    public void A_circle_on_its_own_is_a_disc_and_a_ring_is_two_circles()
    {
        Assert.Equal(Math.PI * 9, Profiles.Region(new List<(Point2, Point2)>(), new List<(Point2, double)> { (P(1, 2), 3) })!.AreaMm2, 9);
        var ring = Profiles.Region(new List<(Point2, Point2)>(), new List<(Point2, double)> { (P(0, 0), 10), (P(0, 0), 4) })!;
        Assert.Equal(Math.PI * (100 - 16), ring.AreaMm2, 9);
        Assert.Equal(0, ring.Centroid.X, 9);
    }

    [Fact]
    public void An_open_chain_or_nothing_has_no_region()
    {
        var open = Rectangle(0, 0, 40, 20);
        open.RemoveAt(2);
        Assert.Null(Profiles.Region(open, NoCircles));
        Assert.Null(Profiles.Region(new List<(Point2, Point2)>(), NoCircles));
    }

    [Fact]
    public void Pappus_turns_area_and_centroid_into_the_revolved_volume()
    {
        // A 10 x 30 rectangle from x = 10 to 20, turned about the y axis: a tube of radii 10 and 20, 30 long.
        var region = Profiles.Region(Rectangle(10, 0, 20, 30), NoCircles)!;
        Assert.Equal(Math.PI * (400 - 100) * 30, FeatureChecks.PappusVolume(region, P(0, 0), P(0, 1), 360), 6);
        Assert.Equal(Math.PI * (400 - 100) * 30 / 4, FeatureChecks.PappusVolume(region, P(0, -5), P(0, 50), 90), 6);
    }

    [Fact]
    public void A_volume_that_changed_the_wrong_way_fails_with_what_was_measured()
    {
        FeatureChecks.VolumeChanged("boss", 8000, 9000, +1);
        FeatureChecks.VolumeChanged("cut", 8000, 7000, -1);
        FeatureChecks.VolumeChanged("plane", 8000, 8000, 0);
        var e = Assert.Throws<SwAgentException>(() => FeatureChecks.VolumeChanged("cut", 8000, 8000, -1));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("volume_change", e.Error.Details!["check"]);
        Assert.Equal(8000.0, e.Error.Details["measured"]);
        Assert.Throws<SwAgentException>(() => FeatureChecks.VolumeChanged("plane", 8000, 8001, 0));
    }

    [Fact]
    public void An_exact_volume_change_must_match_within_tolerance()
    {
        FeatureChecks.VolumeChangeIs("boss", 8000, 8000 * (1 + 1e-7), "800 mm² × 10 mm");
        var e = Assert.Throws<SwAgentException>(() => FeatureChecks.VolumeChangeIs("boss", 8000, 7990, "800 mm² × 10 mm"));
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.Contains("800 mm² × 10 mm", e.Message);
    }

    [Fact]
    public void Directions_compare_as_lines_and_planes_by_offset_and_contained_points()
    {
        Assert.Equal(0, FeatureChecks.AngleDeg(new Vector3(0, 0, 1), new Vector3(0, 0, -1)), 9);
        Assert.Equal(90, FeatureChecks.AngleDeg(new Vector3(1, 0, 0), new Vector3(0, 1, 0)), 9);
        Assert.Equal(30, FeatureChecks.AngleDeg(new Vector3(0, 0, 1), new Vector3(0, Math.Sin(Math.PI / 6), Math.Cos(Math.PI / 6))), 9);
        Assert.Equal(15, FeatureChecks.Offset(new Point3(0, 0, 10), new Vector3(0, 0, 1), new Point3(3, 4, 25)), 9);
        Assert.Equal(0, FeatureChecks.Offset(new Point3(0, 0, 0), new Vector3(1, 0, 0), new Point3(0, 7, 9)), 9);
    }
}
