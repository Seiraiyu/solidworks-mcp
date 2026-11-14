using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>Transforms as SOLIDWORKS gives them for the standard views of a part, with the sheet position of the model's origin.</summary>
static class Views
{
    /// <summary>R's columns: the model directions of the sheet's right, the sheet's up, and toward the reader.</summary>
    public static ViewTransform Of(Vector3 right, Vector3 up, Vector3 facing, double scale, double txMm, double tyMm) =>
        new ViewTransform(new[] { right.X, up.X, facing.X, right.Y, up.Y, facing.Y, right.Z, up.Z, facing.Z }, txMm, tyMm, 0, scale);

    public static ViewTransform Front(double scale = 1, double tx = 0, double ty = 0) =>
        Of(new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), scale, tx, ty);

    public static ViewTransform Top(double scale = 1, double tx = 0, double ty = 0) =>
        Of(new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0), scale, tx, ty);

    public static ViewTransform Right(double scale = 1, double tx = 0, double ty = 0) =>
        Of(new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0), scale, tx, ty);

    public static ViewTransform Isometric(double scale = 1)
    {
        double a = 1 / Math.Sqrt(2), b = 1 / Math.Sqrt(6), c = 1 / Math.Sqrt(3);
        return Of(new Vector3(a, 0, -a), new Vector3(-b, 2 * b, -b), new Vector3(c, c, c), scale, 0, 0);
    }

    /// <summary>The 60 x 40 x 20 mm block of the drawings suite: x 0..60, y 0..40, z 0..20.</summary>
    public static Box3 Block => new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
}

public class ViewGeometryTests
{
    [Fact]
    public void A_model_point_lands_at_its_rotated_position_times_the_scale_plus_the_translation()
    {
        var front = Views.Front(0.5, 100, 150);
        var p = ViewGeometry.ToSheet(front, new Point3(60, 40, 20));
        Assert.Equal(130, p.X, 9);
        Assert.Equal(170, p.Y, 9);
        var top = Views.Top(1, 200, 100);
        var q = ViewGeometry.ToSheet(top, new Point3(10, 40, 20));
        Assert.Equal(210, q.X, 9);
        Assert.Equal(80, q.Y, 9);
    }

    [Fact]
    public void Array_data_is_read_as_rotation_translation_in_metres_and_scale()
    {
        var t = ViewTransform.FromArrayData(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.15, 0, 0.5, 0, 0, 0 })!;
        Assert.Equal(100, t.TxMm, 9);
        Assert.Equal(150, t.TyMm, 9);
        Assert.Equal(0.5, t.Scale);
        Assert.Null(ViewTransform.FromArrayData(new double[] { 1, 0, 0 }));
    }

    [Theory]
    [InlineData("front")]
    [InlineData("top")]
    [InlineData("right")]
    public void The_direction_facing_the_reader_names_the_orthographic_view(string orientation)
    {
        var t = orientation switch { "front" => Views.Front(), "top" => Views.Top(), _ => Views.Right() };
        Assert.Equal(orientation, ViewGeometry.Orientation(ViewGeometry.Facing(t)));
        Assert.Null(ViewGeometry.OrientationMismatch(orientation, t));
    }

    [Fact]
    public void A_view_facing_another_way_says_what_it_faces()
    {
        string? why = ViewGeometry.OrientationMismatch("front", Views.Top());
        Assert.NotNull(why);
        Assert.Contains("+Z (front)", why);
        Assert.Contains("+Y (top)", why);
        Assert.NotNull(ViewGeometry.OrientationMismatch("back", Views.Front()));
    }

    [Fact]
    public void An_isometric_view_faces_a_direction_of_equal_components_and_others_are_not_checked_by_direction()
    {
        var iso = Views.Isometric();
        Assert.Equal("isometric", ViewGeometry.Orientation(ViewGeometry.Facing(iso)));
        Assert.Null(ViewGeometry.OrientationMismatch("isometric", iso));
        Assert.NotNull(ViewGeometry.OrientationMismatch("isometric", Views.Front()));
        Assert.Null(ViewGeometry.OrientationMismatch("trimetric", Views.Front()));
        Assert.Null(ViewGeometry.OrientationMismatch("*Shop view", Views.Front()));
    }

    [Fact]
    public void A_block_projects_to_its_width_and_height_in_each_orthographic_view()
    {
        var front = ViewGeometry.Project(Views.Front(0.5, 100, 150), Views.Block);
        Assert.Equal(100, front.Min.X, 9);
        Assert.Equal(130, front.Max.X, 9);
        Assert.Equal(150, front.Min.Y, 9);
        Assert.Equal(170, front.Max.Y, 9);
        var top = ViewGeometry.Project(Views.Top(), Views.Block);
        Assert.Equal(60, ViewGeometry.Width(top), 9);
        Assert.Equal(20, ViewGeometry.Height(top), 9);
        var right = ViewGeometry.Project(Views.Right(2), Views.Block);
        Assert.Equal(40, ViewGeometry.Width(right), 9);
        Assert.Equal(80, ViewGeometry.Height(right), 9);
    }

    [Fact]
    public void Depth_is_what_a_view_beside_it_shows_across()
    {
        Assert.Equal(10, ViewGeometry.Depth(Views.Front(0.5), Views.Block), 9);
        Assert.Equal(40, ViewGeometry.Depth(Views.Top(), Views.Block), 9);
        Assert.Equal(60, ViewGeometry.Depth(Views.Right(), Views.Block), 9);
    }

    [Fact]
    public void Boxes_compare_within_a_hundredth()
    {
        var a = new SheetBox { Min = new SheetPoint(0, 0), Max = new SheetPoint(60, 40) };
        var b = new SheetBox { Min = new SheetPoint(0.009, -0.009), Max = new SheetPoint(60.009, 40) };
        var moved = new SheetBox { Min = new SheetPoint(10, 10), Max = new SheetPoint(70, 50) };
        Assert.True(ViewGeometry.SameBox(a, b));
        Assert.False(ViewGeometry.SameBox(a, moved));
        Assert.True(ViewGeometry.SameSize(a, moved));
        Assert.True(ViewGeometry.Within(new SheetBox { Min = new SheetPoint(5, 5), Max = new SheetPoint(55, 35) }, a));
        Assert.False(ViewGeometry.Within(moved, a));
        Assert.Equal(30, ViewGeometry.Centre(a).X);
    }
}
