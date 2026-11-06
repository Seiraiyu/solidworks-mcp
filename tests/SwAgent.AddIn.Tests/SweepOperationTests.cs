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
