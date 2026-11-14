using System;
using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ProjectedViewGeometryTests
{
    [Fact]
    public void A_view_from_the_right_or_above_is_the_parent_turned_toward_that_side()
    {
        var front = Views.Front();
        Assert.True(ViewGeometry.Same(new Vector3(1, 0, 0), ViewGeometry.ProjectedFacing(front, ProjectDirection.Right)));
        Assert.True(ViewGeometry.Same(new Vector3(-1, 0, 0), ViewGeometry.ProjectedFacing(front, ProjectDirection.Left)));
        Assert.True(ViewGeometry.Same(new Vector3(0, 1, 0), ViewGeometry.ProjectedFacing(front, ProjectDirection.Above)));
        Assert.True(ViewGeometry.Same(new Vector3(0, -1, 0), ViewGeometry.ProjectedFacing(front, ProjectDirection.Below)));
    }

    [Theory]
    [InlineData(ProjectDirection.Right, Projection.ThirdAngle, 1, 0)]
    [InlineData(ProjectDirection.Right, Projection.FirstAngle, -1, 0)]
    [InlineData(ProjectDirection.Left, Projection.ThirdAngle, -1, 0)]
    [InlineData(ProjectDirection.Above, Projection.ThirdAngle, 0, 1)]
    [InlineData(ProjectDirection.Above, Projection.FirstAngle, 0, -1)]
    [InlineData(ProjectDirection.Below, Projection.FirstAngle, 0, 1)]
    public void The_projection_angle_decides_the_side(ProjectDirection direction, Projection projection, int dx, int dy)
    {
        Assert.Equal((dx, dy), ViewGeometry.Side(direction, projection));
    }

    [Fact]
    public void Without_a_position_a_projected_view_goes_beside_its_parent_with_a_gap()
    {
        // The block's front view at 1:1 centred at (100, 150): 60 x 40 drawn, 20 deep.
        var extents = new SheetBox { Min = new SheetPoint(70, 130), Max = new SheetPoint(130, 170) };
        var right = ViewGeometry.ProjectedPlace(new SheetPoint(100, 150), extents, 20, ProjectDirection.Right, Projection.ThirdAngle, 20);
        Assert.Equal(160, right.X, 9);
        Assert.Equal(150, right.Y, 9);
        var above = ViewGeometry.ProjectedPlace(new SheetPoint(100, 150), extents, 20, ProjectDirection.Above, Projection.FirstAngle, 20);
        Assert.Equal(100, above.X, 9);
        Assert.Equal(100, above.Y, 9);
    }

    [Fact]
    public void A_position_on_the_wrong_side_says_where_the_view_goes()
    {
        var parent = new SheetPoint(100, 150);
        Assert.Null(ViewGeometry.WrongSide(parent, new SheetPoint(160, 150), ProjectDirection.Right, Projection.ThirdAngle));
        string? why = ViewGeometry.WrongSide(parent, new SheetPoint(160, 150), ProjectDirection.Right, Projection.FirstAngle);
        Assert.Equal("a view seen from right goes left of its parent in first_angle projection", why);
        Assert.NotNull(ViewGeometry.WrongSide(parent, new SheetPoint(100, 150), ProjectDirection.Above, Projection.ThirdAngle));
    }

    [Fact]
    public void A_projected_view_is_aligned_when_it_faces_the_turned_way_and_draws_the_origin_on_the_centre_line()
    {
        var front = Views.Front(1, 70, 130);
        Assert.Null(ViewGeometry.AlignmentMismatch(front, Views.Right(1, 170, 130), ProjectDirection.Right));
        Assert.Null(ViewGeometry.AlignmentMismatch(front, Views.Top(1, 70, 200), ProjectDirection.Above));
        Assert.Contains("horizontal centre line", ViewGeometry.AlignmentMismatch(front, Views.Right(1, 170, 135), ProjectDirection.Right));
        Assert.Contains("vertical centre line", ViewGeometry.AlignmentMismatch(front, Views.Top(1, 75, 200), ProjectDirection.Above));
        Assert.Contains("should face", ViewGeometry.AlignmentMismatch(front, Views.Top(1, 70, 200), ProjectDirection.Right));
    }

    /// <summary>One polyline as GetPolylines5 gives it: type, geometry, six style numbers, points in metres.</summary>
    static IEnumerable<double> Edge(double[] geometry, params (double X, double Y)[] pointsMm)
    {
        yield return 1;
        yield return geometry.Length;
        foreach (var g in geometry) yield return g;
        foreach (var s in new double[] { 0, 1, 2, 3, 4, 5 }) yield return s;
        yield return pointsMm.Length;
        foreach (var (x, y) in pointsMm)
        {
            yield return x / 1000;
            yield return y / 1000;
            yield return 0;
        }
    }

    [Fact]
    public void Drawn_extents_are_the_box_around_every_edges_points_placed_at_the_position_and_scaled()
    {
        var data = new List<double>();
        data.AddRange(Edge(new double[] { 9, 9, 9 }, (-30, -20), (30, -20)));
        data.AddRange(Edge(new double[0], (30, -20), (30, 20), (-30, 20)));
        var box = ViewGeometry.DrawnExtents(data, new SheetPoint(100, 150), 0.5)!;
        Assert.Equal(85, box.Min.X, 9);
        Assert.Equal(140, box.Min.Y, 9);
        Assert.Equal(115, box.Max.X, 9);
        Assert.Equal(160, box.Max.Y, 9);
    }

    [Fact]
    public void A_view_that_draws_nothing_has_no_extents_and_short_numbers_are_a_format_error()
    {
        Assert.Null(ViewGeometry.DrawnExtents(new double[0], new SheetPoint(0, 0), 1));
        var data = new List<double>(Edge(new double[0], (1, 1), (2, 2)));
        data.RemoveAt(data.Count - 1);
        Assert.Throws<FormatException>(() => ViewGeometry.DrawnExtents(data, new SheetPoint(0, 0), 1));
        Assert.Throws<FormatException>(() => ViewGeometry.DrawnExtents(new double[] { 1, 2.5 }, new SheetPoint(0, 0), 1));
    }
}
