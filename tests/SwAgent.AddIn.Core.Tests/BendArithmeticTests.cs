using System;
using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class BendArithmeticTests
{
    static BendAllowance K(double k) => new BendAllowance { KFactor = k };

    static Point2 P(double x, double y) => new Point2(x, y);

    /// <summary>
    /// CADapter's measured flat lengths on seiraiyu2 (design 454): a 100 × 60 × 2 mm sheet with one 25 mm edge flange
    /// at R 3 flattens to 77 mm of straight legs (55 + 22) plus one 90° bend.
    /// </summary>
    [Theory]
    [InlineData(0.1, 82.0265)]
    [InlineData(0.5, 83.2832)]
    [InlineData(0.9, 84.5398)]
    public void One_bend_flattens_to_the_measured_length(double k, double measured)
    {
        double flat = BendArithmetic.FlatLengthMm(new[] { 55.0, 22.0 }, new[] { BendArithmetic.AllowanceMm(90, 3, 2, K(k)) });
        Assert.Equal(measured, flat, 4);
    }

    [Fact]
    public void A_box_of_four_20_mm_flanges_flattens_to_the_measured_size()
    {
        double ba = BendArithmetic.AllowanceMm(90, 3, 2, K(0.5));
        Assert.Equal(136.6, BendArithmetic.FlatLengthMm(new[] { 17.0, 90.0, 17.0 }, new[] { ba, ba }), 1);
        Assert.Equal(96.6, BendArithmetic.FlatLengthMm(new[] { 17.0, 50.0, 17.0 }, new[] { ba, ba }), 1);
    }

    [Fact]
    public void An_allowance_given_is_used_whatever_the_angle_and_a_deduction_works_back_to_it()
    {
        Assert.Equal(4.5, BendArithmetic.AllowanceMm(60, 3, 2, new BendAllowance { AllowanceMm = 4.5 }));
        double deduction = BendArithmetic.DeductionMm(90, 3, 2, K(0.4));
        Assert.Equal(2 * 5 - Math.PI / 2 * 3.8, deduction, 9);
        Assert.Equal(BendArithmetic.AllowanceMm(90, 3, 2, K(0.4)), BendArithmetic.AllowanceMm(90, 3, 2, new BendAllowance { DeductionMm = deduction }), 9);
        Assert.Equal(5, BendArithmetic.OutsideSetbackMm(90, 3, 2), 9);
        Assert.Throws<ArgumentException>(() => BendArithmetic.AllowanceMm(90, 3, 2, new BendAllowance()));
    }

    [Fact]
    public void A_bend_holds_the_ring_sector_folded_and_flattening_it_changes_the_volume_except_at_half()
    {
        Assert.Equal(100 * Math.PI / 4 * 16, BendArithmetic.FoldedVolumeMm3(100, 90, 3, 2), 9);
        Assert.Equal(0, BendArithmetic.FlattenChangeMm3(100, 90, 3, 2, K(0.5)), 9);
        Assert.Equal(100 * 2 * (Math.PI / 2) * -0.4 * 2, BendArithmetic.FlattenChangeMm3(100, 90, 3, 2, K(0.1)), 9);
    }

    [Fact]
    public void Lines_in_any_order_and_direction_make_one_chain_from_a_free_end()
    {
        var chain = BendArithmetic.Chain(new List<(Point2, Point2)> { (P(50, 30), P(0, 30)), (P(50, 0), P(50, 30)), (P(0, 0), P(0, 30)) })!;
        Assert.Equal(4, chain.Count);
        Assert.Equal(new[] { "(50, 0)", "(50, 30)", "(0, 30)", "(0, 0)" }, new[] { chain[0].ToString(), chain[1].ToString(), chain[2].ToString(), chain[3].ToString() });
    }

    [Fact]
    public void Loops_branches_and_two_chains_are_not_a_chain()
    {
        Assert.Null(BendArithmetic.Chain(new List<(Point2, Point2)>()));
        Assert.Null(BendArithmetic.Chain(new List<(Point2, Point2)> { (P(0, 0), P(10, 0)), (P(10, 0), P(10, 10)), (P(10, 10), P(0, 0)) }));
        Assert.Null(BendArithmetic.Chain(new List<(Point2, Point2)> { (P(0, 0), P(10, 0)), (P(10, 0), P(20, 0)), (P(10, 0), P(10, 10)) }));
        Assert.Null(BendArithmetic.Chain(new List<(Point2, Point2)> { (P(0, 0), P(10, 0)), (P(20, 0), P(30, 0)) }));
    }

    /// <summary>
    /// A U of 30, 50 and 30 mm turning right twice, R 3, T 2, 20 deep. Material on the left (outside the U): the lines
    /// are the inner face, legs 27, 44 and 27 plus two arcs of radius 4. On the right (inside): the outer face, legs
    /// 25, 40 and 25.
    /// </summary>
    [Fact]
    public void A_U_holds_its_legs_and_middle_arcs_on_either_side()
    {
        var u = BendArithmetic.OpenProfile(new[] { P(0, 0), P(0, 30), P(50, 30), P(50, 0) }, 3, 2, 20);
        Assert.Equal(2, u.Bends);
        Assert.Equal(new[] { 90.0, 90.0 }, u.AnglesDeg);
        Assert.Equal(20 * 2 * (98 + 4 * Math.PI), u.LeftMm3!.Value, 9);
        Assert.Equal(20 * 2 * (90 + 4 * Math.PI), u.RightMm3!.Value, 9);
    }

    [Fact]
    public void Straight_runs_are_no_bend_and_a_side_whose_legs_run_out_is_null()
    {
        var straight = BendArithmetic.OpenProfile(new[] { P(0, 0), P(10, 0), P(30, 0) }, 3, 2, 10);
        Assert.Equal(0, straight.Bends);
        Assert.Equal(10 * 2 * 30, straight.LeftMm3!.Value, 9);
        var stub = BendArithmetic.OpenProfile(new[] { P(0, 0), P(0, 4), P(30, 4) }, 3, 2, 10);
        Assert.Null(stub.RightMm3);
        Assert.NotNull(stub.LeftMm3);
    }
}
