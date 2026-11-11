using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class FlangeGeometryTests
{
    static EdgeFlangeSection Section(double angle, double length, FlangeLengthFrom from = FlangeLengthFrom.InnerVirtualSharp, BendPosition position = BendPosition.MaterialInside) =>
        FlangeGeometry.EdgeFlange(angle, 3, 2, length, from, position)!;

    [Theory]
    [InlineData(0.1, 82.0265)]
    [InlineData(0.5, 83.2832)]
    [InlineData(0.9, 84.5398)]
    public void A_25_mm_flange_on_a_60_mm_sheet_flattens_to_CADapters_measured_length(double k, double flat)
    {
        // CADapter: sheet 100 × 60 × 2, one 25 mm flange at R 3 (drawn 25 mm up from the edge, material inside).
        var section = Section(90, 25);
        Assert.Equal(flat, 60 + section.FlatChangeMm(new BendAllowance { KFactor = k }), 4);
        Assert.Equal(25, section.HeightMm, 9);
        Assert.Equal(5, section.TrimMm, 9);
        Assert.Equal(22, section.StraightMm, 9);
    }

    [Fact]
    public void The_same_flange_measured_from_the_outer_sharp_or_the_tangent_is_the_same_flange()
    {
        var inner = Section(90, 25);
        var outer = Section(90, 27, FlangeLengthFrom.OuterVirtualSharp);
        var tangent = Section(90, 22, FlangeLengthFrom.Tangent);
        foreach (var s in new[] { outer, tangent })
        {
            Assert.Equal(inner.HeightMm, s.HeightMm, 9);
            Assert.Equal(inner.InnerLengthMm, s.InnerLengthMm, 9);
            Assert.Equal(27, s.OuterLengthMm, 9);
            Assert.Equal(inner.AddedPerWidthMm2, s.AddedPerWidthMm2, 9);
        }
    }

    [Fact]
    public void Four_20_mm_flanges_make_CADapters_box_and_its_flat_pattern()
    {
        // CADapter: 100 × 60 × 2 and four 20 mm flanges, longest free edge first: a box 100 × 60 × 22 of 2.2970e-05 m³
        // flattening to 136.6 × 96.6 mm. The two short flanges are made on edges the long ones have cut to 50 mm.
        var section = Section(90, 20);
        double volume = 12000 + (100 + 100 + 50 + 50) * section.AddedPerWidthMm2;
        Assert.Equal(22970, volume, 0);
        Assert.Equal(20, section.RiseMm!.Value, 9);
        var k = new BendAllowance { KFactor = 0.5 };
        Assert.Equal(136.6, 100 + 2 * section.FlatChangeMm(k), 1);
        Assert.Equal(96.6, 60 + 2 * section.FlatChangeMm(k), 1);
    }

    [Fact]
    public void The_position_decides_how_much_sheet_the_bend_takes_back()
    {
        Assert.Equal(5, Section(90, 20, position: BendPosition.MaterialInside).TrimMm, 9);
        Assert.Equal(3, Section(90, 20, position: BendPosition.MaterialOutside).TrimMm, 9);
        Assert.Equal(0, Section(90, 20, position: BendPosition.BendOutside).TrimMm, 9);
        Assert.Equal(2, Section(90, 20, position: BendPosition.MaterialOutside).OuterSharpMm, 9);
        Assert.Equal(5, Section(90, 20, position: BendPosition.BendOutside).OuterSharpMm, 9);
        Assert.All(new[] { BendPosition.MaterialInside, BendPosition.MaterialOutside, BendPosition.BendOutside }, p => Assert.Equal(20, Section(90, 20, position: p).HeightMm, 9));
    }

    [Fact]
    public void A_45_degree_flange_is_worked_out_by_hand()
    {
        // tan 22.5° = 0.414214: the inner sharp is 2 × 0.414214 behind the edge, the profile 20 - 0.828427 × cos 45° long.
        var s = Section(45, 20);
        double t = Math.Tan(Math.PI / 8);
        Assert.Equal(20 - 2 * t * Math.Cos(Math.PI / 4), s.HeightMm, 9);
        Assert.Equal(20 - 3 * t, s.StraightMm, 9);
        Assert.Equal(5 * t, s.TrimMm, 9);
        Assert.Equal(20 * Math.Sin(Math.PI / 4), s.RiseMm!.Value, 9);
        Assert.Equal(2 * (20 - 3 * t - 5 * t) + Math.PI / 8 * (25 - 9), s.AddedPerWidthMm2, 9);
        Assert.Null(Section(120, 20).RiseMm);
    }

    [Fact]
    public void A_length_shorter_than_its_bend_makes_no_flange()
    {
        Assert.Null(FlangeGeometry.EdgeFlange(90, 3, 2, 2, FlangeLengthFrom.InnerVirtualSharp, BendPosition.MaterialInside));
        Assert.Null(FlangeGeometry.EdgeFlange(90, 3, 2, 4, FlangeLengthFrom.OuterVirtualSharp, BendPosition.MaterialInside));
        Assert.NotNull(FlangeGeometry.EdgeFlange(90, 3, 2, 0.5, FlangeLengthFrom.Tangent, BendPosition.MaterialInside));
    }

    [Fact]
    public void The_profile_runs_from_the_edge_away_from_the_material_and_up_the_face_normal()
    {
        var corners = FlangeGeometry.Profile(new Point3(0, 60, 2), new Point3(100, 60, 2), new Vector3(0, 0, 1), new Vector3(0, 1, 0), 90, 25, 10);
        Assert.Equal(new[] { (10.0, 60.0, 2.0), (90.0, 60.0, 2.0), (90.0, 60.0, 27.0), (10.0, 60.0, 27.0) },
            corners.Select(p => (Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9))));
        var tilted = FlangeGeometry.Profile(new Point3(0, 60, 2), new Point3(100, 60, 2), new Vector3(0, 0, 1), new Vector3(0, 1, 0), 45, 10, 0);
        Assert.Equal(60 + 10 * Math.Cos(Math.PI / 4), tilted[2].Y, 9);
        Assert.Equal(2 + 10 * Math.Sin(Math.PI / 4), tilted[2].Z, 9);
        var down = FlangeGeometry.Profile(new Point3(0, 0, 0), new Point3(0, 60, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0), 90, 20, 0);
        Assert.Equal(-20, down[2].Z, 9);
        Assert.Equal(0, down[2].X, 9);
    }

    [Fact]
    public void An_edge_has_two_ends_and_a_profile_touches_one_or_misses_by_a_distance()
    {
        var (start, end) = FlangeGeometry.Ends(new Point3(50, 60, 2), new Vector3(1, 0, 0), 100);
        Assert.Equal(0, start.X, 9);
        Assert.Equal(100, end.X, 9);
        Assert.Equal(0, FlangeGeometry.NearestEndMm(new[] { new Point3(100, 60, 2), new Point3(100, 60, -18) }, start, end), 9);
        Assert.Equal(2, FlangeGeometry.NearestEndMm(new[] { new Point3(100, 60, 0), new Point3(100, 60, -20) }, start, end), 9);
    }

    [Fact]
    public void A_hem_adds_its_fold_and_return_less_what_it_takes_back()
    {
        double fold = Math.PI / 2 * 4;
        var outside = FlangeGeometry.Hem(HemKind.Closed, 2, 100, 10, null, null, null, BendPosition.BendOutside);
        Assert.Equal(100 * (2 * 8 + fold), outside.ExpectedMm3, 9);
        Assert.Equal(100 * 2 * 2, outside.ToleranceMm3, 9);
        var inside = FlangeGeometry.Hem(HemKind.Closed, 2, 100, 10, null, null, null, BendPosition.MaterialInside);
        Assert.Equal(100 * (2 * (8 - 2) + fold), inside.ExpectedMm3, 9);
        var open = FlangeGeometry.Hem(HemKind.Open, 2, 100, 10, 1, null, null, BendPosition.BendOutside);
        Assert.Equal(100 * (2 * 7.5 + Math.PI / 2 * (2.5 * 2.5 - 0.25)), open.ExpectedMm3, 9);
        var rolled = FlangeGeometry.Hem(HemKind.Rolled, 2, 100, null, null, 3, 270, BendPosition.BendOutside);
        Assert.Equal(100 * BendArithmetic.FoldedVolumeMm3(1, 270, 3, 2), rolled.ExpectedMm3, 9);
        Assert.Equal(100 * 2 * 5, rolled.ToleranceMm3, 9);
    }
}
