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
        Assert.Contains("cut into", change.Why);
    }

    [Fact]
    public void A_cut_in_a_bend_is_the_area_its_cylinders_lost_times_the_thickness()
    {
        // A 100 mm wide 90° bend at R 3, T 2: whole, its inner cylinder is 100 × π/2 × 3 and its outer 100 × π/2 × 5.
        double inner = 100 * Math.PI / 2 * 3, outer = 100 * Math.PI / 2 * 5;
        Assert.Equal(0, BendDevelopment.CutFromBend(100, 90, 3, 2, new[] { (3.0, inner), (5.0, outer) })!.Value, 9);
        // A through hole of 12.566 mm² flat at the neutral radius 4: the inner face loses 3/4 of it, the outer 5/4; folded it held 2 × 12.566 mm³.
        double hole = Math.PI * 4;
        double cut = BendDevelopment.CutFromBend(100, 90, 3, 2, new[] { (3.0, inner - hole * 3 / 4), (5.0, outer - hole * 5 / 4) })!.Value;
        Assert.Equal(2 * hole, cut, 9);
        // Cut across, each cylinder in two pieces: the pieces are summed.
        Assert.Equal(2 * hole, BendDevelopment.CutFromBend(100, 90, 3, 2, new[] { (3.0, inner / 2), (3.0, inner / 2 - hole * 3 / 4), (5.0, outer / 2), (5.0, outer / 2 - hole * 5 / 4) })!.Value, 9);
        // An area SOLIDWORKS does not give leaves it unknown.
        Assert.Null(BendDevelopment.CutFromBend(100, 90, 3, 2, new[] { (3.0, inner), (5.0, double.NaN) }));
    }

    [Fact]
    public void A_bend_cut_into_widens_the_bound_by_the_material_the_cut_took()
    {
        // SOLIDWORKS 2026 SP4 folds a hole in a bend 0.2 to 0.9 mm³ off the arithmetic's nothing at K 0.5 (6.C live run).
        var change = BendDevelopment.Develop(new[] { new DevelopedBend("EdgeBend1", 90, 3, K(0.5), 100, whole: false, cutMm3: 25.1) }, 2, flatten: false)!;
        Assert.False(change.Exact);
        Assert.True(change.Holds(-0.857, 1e-5));
        Assert.True(change.Holds(0.187, 1e-5));
        Assert.False(change.Holds(-26, 1e-5));
        Assert.Contains("25.1 mm³", change.Why);
        // The bound is the material the cut took, no wider.
        Assert.False(change.Holds(25.2, 1e-5));
        Assert.True(change.Holds(25.1, 1e-5));
    }

    [Fact]
    public void With_no_cut_in_any_bend_the_check_stays_exact()
    {
        var change = BendDevelopment.Develop(new[] { new DevelopedBend("EdgeBend1", 90, 3, K(0.5), 100, whole: true, cutMm3: 0) }, 2, flatten: false)!;
        Assert.True(change.Exact);
        Assert.True(change.Holds(0, 1e-5));
        Assert.False(change.Holds(-0.857, 1e-5));
        Assert.False(change.Holds(0.187, 1e-5));
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
