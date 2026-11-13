using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// What 6.C's operations share (SheetMetalParts): the bends unfolded now, the face that stays put, the bends read for
/// their development, and the folded-only rule naming the flatten tool. On the 40 × 20 × 2 tab with a 25 mm edge flange
/// on its 40 mm edge at y = 20: its base face's centre is (20, 10, 0).
/// </summary>
public class SheetBendPartsTests : SheetMetalFlangeTestBase
{
    FakeDocument Flanged()
    {
        var doc = Sheet();
        var face = doc.SheetFaceList[0];
        face.Info.Origin = new Point3(20, 10, 0);
        face.Info.DistanceMm = 0;
        EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        return doc;
    }

    [Fact]
    public void An_unfold_opens_its_bends_a_fold_closes_them_and_a_suppressed_one_counts_for_nothing()
    {
        var doc = Flanged();
        var bend = (FakeEntity)doc.FeatureByName("EdgeBend1")!;
        Assert.Empty(SheetMetalParts.Unfolded(doc));
        doc.Unfold(doc.SheetFaceList[0], new object[] { bend });
        var open = SheetMetalParts.Unfolded(doc);
        Assert.Equal("UnFold1", Assert.Single(open).Value.Name);
        Assert.Same(doc.SheetFaceList[0], open["EdgeBend1"].FixedFace);
        doc.Suppress(doc.FeatureByName("UnFold1")!, true);
        Assert.Empty(SheetMetalParts.Unfolded(doc));
        doc.Suppress(doc.FeatureByName("UnFold1")!, false);
        doc.Fold(doc.SheetFaceList[0], new object[] { bend });
        Assert.Empty(SheetMetalParts.Unfolded(doc));
    }

    [Fact]
    public void A_bend_reads_whole_with_its_two_cylinders_cut_across_with_more_and_developed_with_none()
    {
        var doc = Flanged();
        var record = doc.SheetMetal.Bends().Single();
        var whole = Assert.Single(SheetMetalParts.Developed(doc, new[] { record }));
        Assert.True(whole.Whole);
        Assert.Equal(40, whole.WidthMm!.Value, 9);
        Assert.Equal(3, whole.RadiusMm);
        var fake = doc.FakeBends.Single();
        fake.Faces.Add(doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, RadiusMm = 3, Axis = new Vector3(1, 0, 0), Box = fake.Faces[0].Info.Box }));
        Assert.False(SheetMetalParts.Developed(doc, new[] { record }).Single().Whole);
        doc.Unfold(doc.SheetFaceList[0], new object[] { fake.Feature });
        var developed = SheetMetalParts.Developed(doc, new[] { record }).Single();
        Assert.False(developed.Whole);
        Assert.Null(developed.WidthMm);
        Assert.Empty(SheetMetalParts.Cylinders(doc, record));
    }

    [Fact]
    public void A_hole_inside_a_bend_leaves_two_cylinders_but_reads_cut_by_the_area_they_lost()
    {
        var doc = Flanged();
        var record = doc.SheetMetal.Bends().Single();
        var fake = doc.FakeBends.Single();
        double inner = 40 * Math.PI / 2 * 3, outer = 40 * Math.PI / 2 * 5;
        fake.Faces[0].Info.AreaMm2 = inner;
        fake.Faces[1].Info.AreaMm2 = outer;
        var whole = SheetMetalParts.Developed(doc, new[] { record }).Single();
        Assert.True(whole.Whole);
        Assert.Equal(0, whole.CutMm3);
        // A 4 mm hole through the bend: flat 4π mm² at the neutral radius 4, so the inner face loses 3/4 of it and the outer 5/4.
        double hole = Math.PI * 4;
        fake.Faces[0].Info.AreaMm2 = inner - hole * 3 / 4;
        fake.Faces[1].Info.AreaMm2 = outer - hole * 5 / 4;
        var cut = SheetMetalParts.Developed(doc, new[] { record }).Single();
        Assert.False(cut.Whole);
        Assert.Equal(2 * hole, cut.CutMm3, 6);
    }

    [Fact]
    public void The_fixed_face_is_planar_with_its_centre_on_it_and_stays_put_while_a_face_lies_there()
    {
        var doc = Flanged();
        var plane = SheetMetalParts.Fixed(doc, doc.SheetFaceList[0], "fixed_face");
        Assert.Equal((20.0, 10.0, 0.0), (plane.Centre.X, plane.Centre.Y, plane.Centre.Z));
        var checks = new List<string>();
        SheetMetalParts.StaysPut(doc, plane, "unfold", checks);
        Assert.Contains("the fixed face stayed put: a face of the sheet still lies through (20, 10, 0) facing (0, 0, 1)", checks);
        doc.SheetFaceList[0].Info.DistanceMm = 3;
        var moved = Fails(() => SheetMetalParts.StaysPut(doc, plane, "unfold", checks));
        Assert.Equal(ErrorCode.VerificationFailed, moved.Code);
        Assert.Equal("fixed_face", moved.Error.Details!["check"]);
        Assert.Equal(3.0, moved.Error.Details["measured"]);
        var e = Fails(() => SheetMetalParts.Fixed(doc, doc.SheetFaceList[0], "fixed_face"));
        Assert.Equal("fixed_face", e.Error.Details!["field"]);
        Assert.Contains("centre is 3 mm off the face", e.Message);
        var cylinder = doc.FakeBends.Single().Faces[0];
        Assert.Contains("is a cylinder face", Fails(() => SheetMetalParts.Fixed(doc, cylinder, "fixed_face")).Message);
    }

    [Fact]
    public void A_fixed_face_centre_a_fraction_of_a_micron_off_is_on_the_face()
    {
        // 6.D live run: after an unfold and a cut, SOLIDWORKS measured the base face's own centre 0.000x mm off it,
        // and fold refused the face with "0 mm off the face".
        var doc = Flanged();
        doc.SheetFaceList[0].Info.DistanceMm = 0.0004;
        var plane = SheetMetalParts.Fixed(doc, doc.SheetFaceList[0], "fixed_face");
        Assert.Equal((20.0, 10.0, 0.0), (plane.Centre.X, plane.Centre.Y, plane.Centre.Z));
        doc.SheetFaceList[0].Info.DistanceMm = SheetMetalParts.OnFaceMm * 2;
        var e = Fails(() => SheetMetalParts.Fixed(doc, doc.SheetFaceList[0], "fixed_face"));
        Assert.Contains("centre is 0.002 mm off the face", e.Message);
    }

    [Fact]
    public void A_flattened_part_is_told_to_fold_with_the_flatten_tool()
    {
        var doc = Flanged();
        doc.Suppress(doc.FeatureByName("Flat-Pattern1")!, false);
        var e = Fails(() => SheetMetalParts.RequireFolded(doc, "sheetmetal.unfold"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("fold it with sw_sheetmetal_flatten {flat: false} first", e.Message);
    }

    [Fact]
    public void Volume_developed_is_checked_exactly_within_a_bound_or_not_at_all()
    {
        var checks = new List<string>();
        SheetMetalParts.VolumeDeveloped(new SwAgent.AddIn.Core.BendVolumeChange(-50, true, "w"), 1000, 950, "flatten", checks);
        Assert.Contains(checks, c => c.StartsWith("the volume changed by -50 mm³, the bend arithmetic's -50 mm³"));
        var e = Fails(() => SheetMetalParts.VolumeDeveloped(new SwAgent.AddIn.Core.BendVolumeChange(-50, true, "w"), 1000, 960, "flatten", checks));
        Assert.Equal("volume_developed", e.Error.Details!["check"]);
        SheetMetalParts.VolumeDeveloped(new SwAgent.AddIn.Core.BendVolumeChange(-50, false, "w"), 1000, 970, "flatten", checks);
        Assert.Contains(checks, c => c.Contains("bound -50 mm³"));
        SheetMetalParts.VolumeDeveloped(null, 1000, 900, "flatten", checks);
        Assert.Contains(checks, c => c.Contains("not compared"));
    }
}
