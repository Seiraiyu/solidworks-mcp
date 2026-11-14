using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DetailGeometryTests
{
    [Fact]
    public void Drawn_points_are_each_edges_points_at_the_views_position_and_scale()
    {
        // Two edges: a line of two points and an arc of three, metres about the view's position.
        var polylines = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 2, 0.01, 0, 0, -0.01, 0, 0, 2, 1, 7, 0, 1, 2, 3, 4, 5, 3, 0, 0.01, 0, 0.01, 0, 0, 0, -0.01, 0 };
        var points = ViewGeometry.DrawnPoints(polylines, new SheetPoint(100, 50), 2);
        Assert.Equal(5, points.Count);
        Assert.Equal(120, points[0].X, 9);
        Assert.Equal(80, points[1].X, 9);
        Assert.Equal(70, points[2].Y, 9);
        Assert.Equal(30, points[4].Y, 9);
        var box = ViewGeometry.DrawnExtents(polylines, new SheetPoint(100, 50), 2)!;
        Assert.Equal(80, box.Min.X, 9);
        Assert.Equal(70, box.Max.Y, 9);
    }

    [Fact]
    public void Without_a_position_a_detail_goes_right_of_its_parent_with_a_gap()
    {
        var at = ViewCutGeometry.DetailPlace(new SheetBox { Min = new SheetPoint(90, 160), Max = new SheetPoint(150, 200) }, 15, 20);
        Assert.Equal(185, at.X, 9);
        Assert.Equal(180, at.Y, 9);
    }

    [Fact]
    public void A_detail_is_turned_as_its_parent()
    {
        Assert.Null(ViewCutGeometry.DetailTurnMismatch(Views.Front(1, 90, 160), Views.Front(2, 300, 100)));
        Assert.Contains("turned right", ViewCutGeometry.DetailTurnMismatch(Views.Front(), Views.Top()));
    }

    [Theory]
    [InlineData("front")]
    [InlineData("top")]
    [InlineData("right")]
    public void A_detail_draws_what_its_parent_draws_at_a_point_where_its_transform_says(string turned)
    {
        // The parent at 1:1 with its origin at (90, 160); the detail at 2:1 with its origin at (250, 60).
        var (parent, detail) = turned switch
        {
            "top" => (Views.Top(1, 90, 160), Views.Top(2, 250, 60)),
            "right" => (Views.Right(1, 90, 160), Views.Right(2, 250, 60)),
            _ => (Views.Front(1, 90, 160), Views.Front(2, 250, 60)),
        };
        // A model point the parent draws at (110, 180): the detail draws it at its origin + 2 x (20, 20), whatever its depth.
        var at = ViewCutGeometry.DetailPoint(parent, detail, new SheetPoint(110, 180));
        Assert.Equal(290, at.X, 9);
        Assert.Equal(100, at.Y, 9);
    }

    [Fact]
    public void Points_inside_the_circle_pass_and_the_farthest_outside_is_named()
    {
        var centre = new SheetPoint(300, 100);
        var inside = new List<SheetPoint> { new SheetPoint(315, 100), new SheetPoint(300, 85.005) };
        Assert.Null(ViewCutGeometry.OutsideCircle(inside, centre, 15));
        Assert.Contains("draws nothing", ViewCutGeometry.OutsideCircle(new List<SheetPoint>(), centre, 15));
        var outside = new List<SheetPoint> { new SheetPoint(310, 100), new SheetPoint(300, 120) };
        Assert.Contains("(300, 120), 20 mm from", ViewCutGeometry.OutsideCircle(outside, centre, 15));
    }
}
