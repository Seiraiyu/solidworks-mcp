using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The block with a 5 mm radius through hole cut at (20, 10): the seed the pattern tests repeat.</summary>
public abstract class PatternTestBase : FeatureTestBase
{
    /// <summary>The volume one hole takes out of the 10 mm block, mm³.</summary>
    protected static readonly double HoleVolume = Math.PI * 25 * 10;

    internal FakeDocument Holed()
    {
        var doc = Part();
        Block();
        Hole();
        Extrude(new FeatureExtrudeRequest { Sketch = "Sketch2", Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        return doc;
    }

    /// <summary>A straight edge of the block along x.</summary>
    internal string XEdge(FakeDocument doc) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 40, Axis = new Vector3(1, 0, 0), Feature = "Boss-Extrude1" })).Reference!;
}

public class FeatureLinearPatternTests : PatternTestBase
{
    internal FeatureResult Pattern(FeatureLinearPatternRequest request) => Run(new FeatureLinearPatternOperation(Services), request);

    [Fact]
    public void Each_new_instance_takes_the_seeds_volume_again()
    {
        var doc = Holed();
        var edge = XEdge(doc);
        var made = Pattern(new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Direction = edge, SpacingMm = 8, Count = 3 });
        Assert.Equal("LPattern1", made.Feature.Name);
        Assert.Equal(8000 - 3 * HoleVolume, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c == "3 instances, the seed included");
        Assert.Contains(made.Checks, c => c.Contains("2 new instances × the seeds' -785.398 mm³"));
        var spec = doc.LinearPatterns.Single();
        Assert.Equal((8.0, 3, false, 1), (spec.SpacingMm, spec.Count, spec.Reverse, spec.Count2));
        Assert.Null(spec.Direction2);
        Assert.Equal("Cut-Extrude1", doc.Describe(spec.Seeds.Single()).Name);
        Assert.Equal(new[] { ("Cut-Extrude1", true), ("Cut-Extrude1", false) }, doc.Suppressions.ToArray());
        Assert.False(doc.Tree.Single(f => f.Name == "Cut-Extrude1").Suppressed);
    }

    [Fact]
    public void A_grid_counts_both_directions()
    {
        var doc = Holed();
        var edge = XEdge(doc);
        var made = Pattern(new FeatureLinearPatternRequest
        {
            Features = new List<string> { "Cut-Extrude1" },
            Direction = edge,
            SpacingMm = 8,
            Count = 3,
            Direction2 = new PatternDirection { Direction = "right", SpacingMm = 6, Count = 2, Reverse = true },
        });
        Assert.Equal(8000 - 6 * HoleVolume, made.Change.VolumeAfterMm3, 6);
        var spec = doc.LinearPatterns.Single();
        Assert.Equal((6.0, 2, true), (spec.SpacingMm2, spec.Count2, spec.Reverse2));
        Assert.Equal("Ebene rechts", doc.Describe(spec.Direction2!).Name);
    }

    [Fact]
    public void A_seed_with_features_built_on_it_is_not_suppressed_and_only_the_change_is_checked()
    {
        var doc = Holed();
        doc.Children["Cut-Extrude1"] = new List<string> { "Fillet1" };
        var made = Pattern(new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Direction = XEdge(doc), SpacingMm = 8, Count = 2 });
        Assert.Empty(doc.Suppressions);
        Assert.Contains(made.Checks, c => c.Contains("seeds that features are built on"));
    }

    [Fact]
    public void A_pattern_with_other_instances_or_another_volume_is_rolled_back()
    {
        var doc = Holed();
        var edge = XEdge(doc);
        doc.InstanceError = 1;
        var e = Fails(() => Pattern(new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Direction = edge, SpacingMm = 8, Count = 3 }));
        Assert.Equal("instances", e.Error.Details!["check"]);
        Assert.Equal(4, e.Error.Details["measured"]);
        doc.InstanceError = 0;
        doc.FeatureVolumeError = 50;
        e = Fails(() => Pattern(new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Direction = edge, SpacingMm = 8, Count = 3 }));
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.Contains("runs off the part", e.Message);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "LPattern");
        Assert.Equal(8000 - HoleVolume, doc.MassProperties!.VolumeMm3, 6);
    }

    [Fact]
    public void A_face_pattern_is_a_geometry_pattern_checked_by_instances_and_a_volume_that_moved()
    {
        var doc = Holed();
        var bore = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Feature = "Cut-Extrude1" })).Reference!;
        var made = Pattern(new FeatureLinearPatternRequest { Faces = new List<string> { bore }, Direction = XEdge(doc), SpacingMm = 8, Count = 3 });
        Assert.Equal(8000 - HoleVolume - 2 * 50, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c == "3 instances, the seed included");
        Assert.Contains(made.Checks, c => c.StartsWith("the volume changed"));
        var spec = doc.LinearPatterns.Single();
        Assert.Empty(spec.Seeds);
        Assert.Single(spec.Faces);
        Assert.Empty(doc.Suppressions);
    }

    [Fact]
    public void A_face_pattern_that_changes_nothing_is_rolled_back()
    {
        var doc = Holed();
        var bore = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder })).Reference!;
        doc.FaceInstanceVolume = 0;
        var e = Fails(() => Pattern(new FeatureLinearPatternRequest { Faces = new List<string> { bore }, Direction = XEdge(doc), SpacingMm = 8, Count = 3 }));
        Assert.Equal("volume_change", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "LPattern");
    }

    [Fact]
    public void Features_and_faces_together_or_neither_are_refused()
    {
        var doc = Holed();
        var edge = XEdge(doc);
        var bore = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder })).Reference!;
        Assert.Equal("features", Fails(() => Pattern(new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Faces = new List<string> { bore }, Direction = edge, SpacingMm = 8, Count = 3 })).Error.Details!["field"]);
        Assert.Equal("features", Fails(() => Pattern(new FeatureLinearPatternRequest { Direction = edge, SpacingMm = 8, Count = 3 })).Error.Details!["field"]);
        Assert.Equal("/faces/1", Fails(() => Pattern(new FeatureLinearPatternRequest { Faces = new List<string> { bore, bore }, Direction = edge, SpacingMm = 8, Count = 3 })).Error.Details!["field"]);
    }

    [Fact]
    public void Requests_that_cannot_be_patterned_name_their_field()
    {
        var doc = Holed();
        var edge = XEdge(doc);
        var circle = Services.References.Describe(doc, doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle })).Reference!;
        FeatureLinearPatternRequest R() => new FeatureLinearPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Direction = edge, SpacingMm = 8, Count = 3 };
        string Field(Action<FeatureLinearPatternRequest> change)
        {
            var r = R();
            change(r);
            return (string)Fails(() => Pattern(r)).Error.Details!["field"]!;
        }
        Assert.Equal("spacing_mm", Field(r => r.SpacingMm = null));
        Assert.Equal("count", Field(r => r.Count = 1));
        Assert.Equal("count", Field(r => r.Count = 501));
        Assert.Equal("features", Field(r => r.Features!.Clear()));
        Assert.Equal("direction", Field(r => r.Direction = circle));
        Assert.Equal("/direction2/count", Field(r => r.Direction2 = new PatternDirection { Direction = "right", SpacingMm = 5 }));
        Assert.Equal("count", Field(r => { r.Count = 100; r.Direction2 = new PatternDirection { Direction = "right", SpacingMm = 5, Count = 6 }; }));
        doc.Suppress(doc.FeatureByName("Cut-Extrude1")!, true);
        Assert.Equal("/features/0", Field(_ => { }));
    }
}

public class FeatureCircularPatternTests : PatternTestBase
{
    internal FeatureResult Pattern(FeatureCircularPatternRequest request) => Run(new FeatureCircularPatternOperation(Services), request);

    /// <summary>A reference axis of the part, as refgeom.axis would make it.</summary>
    internal string Axis(FakeDocument doc)
    {
        var axis = doc.AddFeature("Axis1", "RefAxis", ReferenceKind.Axis);
        axis.Info.Geometry = GeometryType.Line;
        axis.Info.Axis = new Vector3(0, 0, 1);
        axis.Info.Origin = new Point3(20, 10, 0);
        return Services.References.Describe(doc, axis).Reference!;
    }

    [Fact]
    public void Instances_shared_round_the_axis_take_the_seeds_volume_each()
    {
        var doc = Holed();
        var made = Pattern(new FeatureCircularPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Axis = Axis(doc), Count = 4 });
        Assert.Equal("CirPattern1", made.Feature.Name);
        Assert.Equal(8000 - 4 * HoleVolume, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c == "4 instances, the seed included");
        var spec = doc.CircularPatterns.Single();
        Assert.Equal((4, 360.0, true, false), (spec.Count, spec.AngleDeg, spec.EqualSpacing, spec.Reverse));
        Assert.Equal("Axis1", doc.Describe(spec.Axis).Name);
    }

    [Fact]
    public void Without_equal_spacing_the_angle_is_between_neighbours()
    {
        var doc = Holed();
        var cylinder = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Axis = new Vector3(0, 0, 1) })).Reference!;
        Pattern(new FeatureCircularPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Axis = cylinder, Count = 3, AngleDeg = 30, EqualSpacing = false, Reverse = true });
        var spec = doc.CircularPatterns.Single();
        Assert.Equal((3, 30.0, false, true), (spec.Count, spec.AngleDeg, spec.EqualSpacing, spec.Reverse));
    }

    [Fact]
    public void A_face_pattern_round_an_axis_is_a_geometry_pattern()
    {
        var doc = Holed();
        var bore = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder })).Reference!;
        var made = Pattern(new FeatureCircularPatternRequest { Faces = new List<string> { bore }, Axis = Axis(doc), Count = 4 });
        Assert.Equal(8000 - HoleVolume - 3 * 50, made.Change.VolumeAfterMm3, 6);
        Assert.Single(doc.CircularPatterns.Single().Faces);
        Assert.Equal("features", Fails(() => Pattern(new FeatureCircularPatternRequest { Axis = Axis(doc), Count = 4 })).Error.Details!["field"]);
    }

    [Fact]
    public void A_pattern_of_another_volume_is_rolled_back()
    {
        var doc = Holed();
        doc.FeatureVolumeError = -10;
        var e = Fails(() => Pattern(new FeatureCircularPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Axis = Axis(doc), Count = 4 }));
        Assert.Equal("volume_exact", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "CirPattern");
    }

    [Fact]
    public void Requests_that_cannot_be_patterned_name_their_field()
    {
        var doc = Holed();
        var axis = Axis(doc);
        var plane = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane })).Reference!;
        FeatureCircularPatternRequest R() => new FeatureCircularPatternRequest { Features = new List<string> { "Cut-Extrude1" }, Axis = axis, Count = 4 };
        string Field(Action<FeatureCircularPatternRequest> change)
        {
            var r = R();
            change(r);
            return (string)Fails(() => Pattern(r)).Error.Details!["field"]!;
        }
        Assert.Equal("count", Field(r => r.Count = null));
        Assert.Equal("angle_deg", Field(r => r.AngleDeg = 361));
        Assert.Equal("angle_deg", Field(r => r.EqualSpacing = false));
        Assert.Equal("angle_deg", Field(r => { r.EqualSpacing = false; r.AngleDeg = 120; }));
        Assert.Equal("axis", Field(r => r.Axis = plane));
        Assert.Equal("/features/0", Field(r => r.Features = new List<string> { "Axis1" }));
    }
}

public class FeatureMirrorTests : PatternTestBase
{
    internal FeatureResult Mirror(FeatureMirrorRequest request) => Run(new FeatureMirrorOperation(Services), request);

    [Fact]
    public void A_mirrored_feature_takes_its_volume_once_more()
    {
        var doc = Holed();
        var made = Mirror(new FeatureMirrorRequest { Features = new List<string> { "Cut-Extrude1" }, Plane = "right" });
        Assert.Equal("Mirror1", made.Feature.Name);
        Assert.Equal(8000 - 2 * HoleVolume, made.Change.VolumeAfterMm3, 6);
        Assert.Contains(made.Checks, c => c.Contains("1 new instances × the seeds' -785.398 mm³"));
        Assert.DoesNotContain(made.Checks, c => c.Contains("instances, the seed included"));
        var spec = doc.Mirrors.Single();
        Assert.False(spec.Body);
        Assert.Equal("Ebene rechts", doc.Describe(spec.Plane).Name);
    }

    [Fact]
    public void A_mirrored_body_doubles_the_part_at_most()
    {
        var doc = Part();
        Block();
        var made = Mirror(new FeatureMirrorRequest { Body = true, Plane = "front" });
        Assert.Equal(16000, made.Change.VolumeAfterMm3, 6);
        Assert.Equal("MirrorSolid", doc.Tree.Single(f => f.Name == "Mirror1").Type);
        Assert.Contains(made.Checks, c => c.StartsWith("the volume at most doubled"));
        Assert.True(doc.Mirrors.Single().Body);
        doc.FeatureVolumeError = 100;
        var e = Fails(() => Mirror(new FeatureMirrorRequest { Body = true, Plane = "front" }));
        Assert.Equal("volume_change", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Mirror2");
    }

    [Fact]
    public void Requests_that_cannot_be_mirrored_name_their_field()
    {
        var doc = Holed();
        var cylinder = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder })).Reference!;
        Assert.Equal("features", Fails(() => Mirror(new FeatureMirrorRequest { Plane = "right" })).Error.Details!["field"]);
        Assert.Equal("features", Fails(() => Mirror(new FeatureMirrorRequest { Features = new List<string> { "Cut-Extrude1" }, Body = true, Plane = "right" })).Error.Details!["field"]);
        Assert.Equal("plane", Fails(() => Mirror(new FeatureMirrorRequest { Features = new List<string> { "Cut-Extrude1" }, Plane = cylinder })).Error.Details!["field"]);
        Assert.Equal("/features/1", Fails(() => Mirror(new FeatureMirrorRequest { Features = new List<string> { "Cut-Extrude1", "Cut-Extrude1" }, Plane = "right" })).Error.Details!["field"]);
    }

    [Fact]
    public void A_body_mirror_on_an_empty_part_is_refused()
    {
        Part();
        Assert.Equal("body", Fails(() => Mirror(new FeatureMirrorRequest { Body = true, Plane = "front" })).Error.Details!["field"]);
    }
}
