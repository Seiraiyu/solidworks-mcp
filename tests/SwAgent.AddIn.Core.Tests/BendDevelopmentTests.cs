using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class BendDevelopmentTests
{
    static BendAllowance K(double k) => new BendAllowance { KFactor = k };

    static DevelopedBend Bend(double width, double k, bool whole = true, double angle = 90) => new DevelopedBend("EdgeBend1", angle, 3, K(k), width, whole);

    [Fact]
    public void Flattening_whole_bends_changes_the_volume_by_exactly_the_bend_arithmetics_sum()
    {
        // The channel at K 0.1 (6.A's U of 30/50/30, 20 deep, R 3, T 2): each bend develops 0.8 × π/2 mm short of its middle arc.
        var change = BendDevelopment.Develop(new[] { Bend(20, 0.1), Bend(20, 0.1) }, 2, flatten: true)!;
        Assert.True(change.Exact);
        Assert.Equal(2 * 20 * 2 * (Math.PI / 2 * 3.2 - Math.PI / 2 * 4), change.ExpectedMm3, 9);
        Assert.Equal(-100.530965, change.ExpectedMm3, 6);
        Assert.True(change.Holds(-100.530965, 1e-5));
        Assert.False(change.Holds(-100.5, 1e-5));
        Assert.Contains("2 bends", change.Why);
    }

    [Fact]
    public void Folding_is_the_same_sum_the_other_way_and_nothing_at_half()
    {
        var fold = BendDevelopment.Develop(new[] { Bend(60, 0.1) }, 2, flatten: false)!;
        // A sketched bend across the 60 mm tab at K 0.1: 60 × 2 × π/2 × (4 − 3.2) more material folded than flat.
        Assert.Equal(60 * 2 * Math.PI / 2 * 0.8, fold.ExpectedMm3, 9);
        var half = BendDevelopment.Develop(new[] { Bend(100, 0.5), Bend(60, 0.5) }, 2, flatten: true)!;
        Assert.Equal(0, half.ExpectedMm3, 9);
        Assert.True(half.Holds(1e-7, 1e-5));
    }

    [Fact]
    public void A_bend_cut_across_bounds_the_change_between_nothing_and_the_whole_bends_sum()
    {
        var change = BendDevelopment.Develop(new[] { Bend(20, 0.1, whole: false) }, 2, flatten: true)!;
        Assert.False(change.Exact);
        Assert.True(change.Holds(-10, 1e-5));
        Assert.True(change.Holds(0, 1e-5));
        Assert.True(change.Holds(-50.265482, 1e-5));
        Assert.False(change.Holds(-51, 1e-5));
        Assert.False(change.Holds(1, 1e-5));
        Assert.Contains("cut across", change.Why);
    }

    [Fact]
    public void A_bend_whose_width_is_unknown_leaves_nothing_to_compare_and_no_bends_change_nothing()
    {
        Assert.Null(BendDevelopment.Develop(new[] { new DevelopedBend("SketchBend1", 90, 3, K(0.1), null, true) }, 2, flatten: true));
        var none = BendDevelopment.Develop(Array.Empty<DevelopedBend>(), 2, flatten: true)!;
        Assert.True(none.Exact);
        Assert.Equal(0, none.ExpectedMm3);
    }

    [Fact]
    public void A_bend_from_a_table_cannot_be_developed()
    {
        Assert.Null(BendDevelopment.Develop(new[] { new DevelopedBend("EdgeBend1", 90, 3, new BendAllowance(), 20, true) }, 2, flatten: true));
    }

    [Fact]
    public void CADapters_L_flattens_to_its_inner_legs_less_the_radius_plus_one_allowance()
    {
        // An L of 60 × 40, 2 mm wall, converted from its inner faces (58 and 38 mm) at R 3: CADapter measured 96.3 mm.
        double flat = BendArithmetic.FlatLengthMm(new[] { 58.0 - 3, 38.0 - 3 }, new[] { BendArithmetic.AllowanceMm(90, 3, 2, K(0.5)) });
        Assert.Equal(96.2832, flat, 4);
    }

    [Fact]
    public void An_inner_corner_is_concave_and_an_outer_one_is_not()
    {
        // The L of 60 × 40 with a 2 mm wall, its corner at the origin: the inner corner edge runs along z at (2, 2).
        var axis = new Vector3(0, 0, 1);
        var inner = new Point3(2, 2, 25);
        var floor = (Normal: new Vector3(0, 1, 0), Inside: new Point3(31, 2, 25));
        var wall = (Normal: new Vector3(1, 0, 0), Inside: new Point3(2, 21, 25));
        Assert.True(ConvertGeometry.Concave(inner, axis, floor.Normal, floor.Inside, wall.Normal, wall.Inside));
        Assert.True(ConvertGeometry.Concave(inner, axis, wall.Normal, wall.Inside, floor.Normal, floor.Inside));
        var outer = new Point3(0, 0, 25);
        var bottom = (Normal: new Vector3(0, -1, 0), Inside: new Point3(30, 0, 25));
        var back = (Normal: new Vector3(-1, 0, 0), Inside: new Point3(0, 20, 25));
        Assert.False(ConvertGeometry.Concave(outer, axis, bottom.Normal, bottom.Inside, back.Normal, back.Inside));
        // Two faces in one plane make no corner at all.
        Assert.False(ConvertGeometry.Concave(inner, axis, floor.Normal, floor.Inside, floor.Normal, new Point3(1, 2, 25)));
    }
}
