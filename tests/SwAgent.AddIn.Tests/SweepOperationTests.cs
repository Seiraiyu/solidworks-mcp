using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class FeatureSweepTests : FeatureTestBase
{
    internal FeatureResult Sweep(FeatureSweepRequest request) => Run(new FeatureSweepOperation(Services), request);

    /// <summary>A 30 mm line on the top plane from the origin, the next sketch: on top, the sketch's y runs along -z, so it goes +z.</summary>
    internal SketchCreateResult Path() => Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "path", Start = P(0, 0), End = P(0, -30) } } }, on: "top");

    [Fact]
    public void A_boss_along_a_line_on_an_empty_part_adds_exactly_area_times_length_along_the_normal()
    {
        var doc = Part();
        Path();
        var made = Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2" });
        Assert.Equal("Sweep1", made.Feature.Name);
        Assert.Equal(24000, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("800 mm² × 30 mm"));
        var (profile, spec) = doc.Sweeps.Single();
        Assert.Equal("Sketch1", doc.Describe(profile).Name);
        Assert.Equal("Sketch2", doc.Describe(spec.Path).Name);
        Assert.False(spec.Cut);
        Assert.True(spec.Merge);
        Assert.Empty(spec.Guides);
    }

    [Fact]
    public void A_cut_along_an_edge_is_checked_by_the_sign_of_its_volume()
    {
        var doc = Part();
        Block();
        Hole();
        var edge = Edge(doc);
        var cut = Sweep(new FeatureSweepRequest { Profile = "Sketch2", Path = edge, Mode = FeatureMode.Cut });
        Assert.Equal("Cut-Sweep1", cut.Feature.Name);
        Assert.Equal("SweepCut", doc.Tree.Single(f => f.Name == "Cut-Sweep1").Type);
        Assert.Equal(8000 - Math.PI * 25 * 10, cut.Change.VolumeAfterMm3, 6);
        Assert.Contains(cut.Checks, c => c.StartsWith("the volume shrank"));
        Assert.DoesNotContain(cut.Checks, c => c.Contains("along its normal"));
    }

    [Fact]
    public void Guides_are_resolved_in_order()
    {
        var doc = Part();
        Path();
        Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "g", Start = P(40, 0), End = P(40, -30) } } }, on: "top");
        Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2", Guides = new List<string> { "Sketch3" } });
        Assert.Equal("Sketch3", doc.Describe(doc.Sweeps.Single().Spec.Guides.Single()).Name);
    }

    [Fact]
    public void Requests_that_cannot_be_swept_name_their_field()
    {
        Part();
        Path();
        Assert.Equal("merge", Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2", Mode = FeatureMode.Cut, Merge = true })).Error.Details!["field"]);
        Assert.Equal("path", Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch1" })).Error.Details!["field"]);
        Assert.Equal("/guides/0", Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2", Guides = new List<string> { "Sketch2" } })).Error.Details!["field"]);
        var e = Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch9" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("path", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_sweep_of_the_wrong_volume_is_rolled_back_and_its_sketches_stay()
    {
        var doc = Part();
        Path();
        doc.FeatureVolumeError = 5;
        var e = Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2" }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sweep1");
        Assert.Contains(doc.Tree, f => f.Name == "Sketch2");
        Assert.Equal(0, doc.MassProperties!.VolumeMm3, 6);
    }

    [Fact]
    public void A_sweep_that_leaves_no_body_fails_the_body_check()
    {
        var doc = Part();
        Path();
        doc.FeatureAddsNothing = true;
        var e = Fails(() => Sweep(new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2" }));
        Assert.Equal("body", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sweep1");
    }
}

public class FeatureLoftTests : FeatureTestBase
{
    internal FeatureResult Loft(FeatureLoftRequest request) => Run(new FeatureLoftOperation(Services), request);

    /// <summary>A 20 x 10 rectangle, the next sketch (on the front plane too: the fake does not mind that it shares a plane).</summary>
    internal SketchCreateResult Small() => Sketch(new SketchGeometry { Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "small", Corner1 = P(10, 5), Corner2 = P(30, 15) } } });

    [Fact]
    public void A_boss_through_two_profiles_grows_the_part_and_passes_them_in_order()
    {
        var doc = Part();
        Small();
        var made = Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch2", "Sketch1" } });
        Assert.Equal("Loft1", made.Feature.Name);
        Assert.Equal("Blend", doc.Tree.Single(f => f.Name == "Loft1").Type);
        Assert.Equal((800 + 200) / 2.0 * 10, made.Change.VolumeAfterMm3, 6);
        Assert.Equal(new[] { "Sketch2", "Sketch1" }, doc.Lofts.Single().Profiles.Select(p => doc.Describe(p).Name).ToArray());
        Assert.Contains(made.Checks, c => c.StartsWith("the volume grew"));
    }

    [Fact]
    public void A_cut_loft_is_a_BlendCut_that_shrinks_the_part()
    {
        var doc = Part();
        Block();
        Small();
        Hole();
        var cut = Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch2", "Sketch3" }, Mode = FeatureMode.Cut });
        Assert.Equal("BlendCut", doc.Tree.Single(f => f.Name == cut.Feature.Name).Type);
        Assert.True(cut.Change.VolumeAfterMm3 < 8000);
        Assert.True(doc.Lofts.Single().Cut);
    }

    [Fact]
    public void Requests_that_cannot_be_lofted_name_their_field()
    {
        Part();
        Small();
        Sketch(new SketchGeometry { CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "c", Start = P(0, 0), End = P(0, 10) } } });
        Assert.Equal("profiles", Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1" } })).Error.Details!["field"]);
        Assert.Equal("/profiles/1", Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch1" } })).Error.Details!["field"]);
        Assert.Equal("/profiles/1", Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch3" } })).Error.Details!["field"]);
        Assert.Equal("/guides/0", Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch2" }, Guides = new List<string> { "Sketch2" } })).Error.Details!["field"]);
        Assert.Equal("merge", Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch2" }, Mode = FeatureMode.Cut, Merge = false })).Error.Details!["field"]);
    }

    [Fact]
    public void A_loft_that_changes_nothing_is_rolled_back()
    {
        var doc = Part();
        Small();
        doc.FeatureAddsNothing = true;
        var e = Fails(() => Loft(new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch2" } }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Loft1");
    }
}
