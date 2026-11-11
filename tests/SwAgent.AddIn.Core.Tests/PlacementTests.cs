using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PlacementTests
{
    [Fact]
    public void No_axes_keeps_the_assemblys_and_no_origin_is_the_assembly_origin()
    {
        var t = Placement.Make(null, null, null, "/x_axis", "/z_axis");
        Assert.Equal(0, t.Origin.X);
        Assert.Equal(1, t.XAxis.X);
        Assert.Equal(1, t.YAxis.Y);
        Assert.Equal(1, t.ZAxis.Z);
    }

    [Fact]
    public void Both_axes_are_normalised_and_y_completes_a_right_handed_frame()
    {
        var t = Placement.Make(new Point3(10, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 5), "/x_axis", "/z_axis");
        Assert.Equal(1, t.XAxis.Y, 12);
        Assert.Equal(1, t.ZAxis.Z, 12);
        Assert.Equal(-1, t.YAxis.X, 12);
        Assert.Equal(10, t.Origin.X);
    }

    [Fact]
    public void One_axis_alone_is_refused_naming_the_missing_one()
    {
        var e = Assert.Throws<SwAgentException>(() => Placement.Make(null, new Vector3(1, 0, 0), null, "/x_axis", "/z_axis"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/z_axis", e.Error.Details!["field"]);
    }

    [Fact]
    public void Axes_that_are_not_perpendicular_are_refused()
    {
        var e = Assert.Throws<SwAgentException>(() => Placement.Make(null, new Vector3(1, 0, 0), new Vector3(1, 0, 1), "/x_axis", "/z_axis"));
        Assert.Equal("/z_axis", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_zero_axis_is_refused()
    {
        var e = Assert.Throws<SwAgentException>(() => Placement.Make(null, new Vector3(0, 0, 0), new Vector3(0, 0, 1), "/x_axis", "/z_axis"));
        Assert.Equal("/x_axis", e.Error.Details!["field"]);
    }

    [Fact]
    public void The_array_holds_the_axes_then_the_origin_in_metres_and_round_trips()
    {
        var t = Placement.Make(new Point3(10, 20, 30), new Vector3(0, 1, 0), new Vector3(0, 0, 1), "x", "z");
        var a = Placement.ToArray(t);
        Assert.Equal(16, a.Length);
        Assert.Equal(new[] { 0.0, 1, 0 }, new[] { a[0], a[1], a[2] });
        Assert.Equal(new[] { 0.01, 0.02, 0.03 }, new[] { a[9], a[10], a[11] });
        Assert.Equal(1, a[12]);
        var back = Placement.FromArray(a);
        Assert.False(Placement.Moved(t, back));
    }

    [Fact]
    public void Moves_below_the_tolerances_do_not_count()
    {
        var a = new Transform { Origin = new Point3(1, 2, 3) };
        var b = new Transform { Origin = new Point3(1.0004, 2, 3) };
        Assert.False(Placement.Moved(a, b));
        b.Origin.Z = 3.01;
        Assert.True(Placement.OriginMoved(a, b));
        var c = new Transform { Origin = new Point3(1, 2, 3), XAxis = new Vector3(0.99999, 0.0044, 0) };
        Assert.True(Placement.Rotated(a, c));
        Assert.False(Placement.OriginMoved(a, c));
    }

    [Fact]
    public void A_short_array_is_an_api_error()
    {
        Assert.Equal(ErrorCode.ApiError, Assert.Throws<SwAgentException>(() => Placement.FromArray(new double[9])).Code);
    }
}
