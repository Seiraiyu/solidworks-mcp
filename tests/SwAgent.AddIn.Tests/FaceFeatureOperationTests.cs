using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class FeatureShellTests : FeatureTestBase
{
    internal FeatureResult Shell(FeatureShellRequest request) => Run(new FeatureShellOperation(Services), request);

    [Fact]
    public void An_inward_shell_opens_its_faces_and_shrinks_the_part()
    {
        var doc = Part();
        Block();
        var top = Face(doc);
        var shell = Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top }, ThicknessMm = 2 });
        Assert.Equal("Shell1", shell.Feature.Name);
        Assert.Equal(6000, shell.Change.VolumeAfterMm3, 6);
        Assert.Contains(shell.Checks, c => c.Contains("the 1 faces named in faces_to_remove are gone"));
        var (faces, thickness, outward) = doc.Shells.Single();
        Assert.Equal((1, 2.0, false), (faces.Count, thickness, outward));
    }

    [Fact]
    public void An_outward_shell_with_no_open_face_grows_the_part()
    {
        var doc = Part();
        Block();
        var shell = Shell(new FeatureShellRequest { ThicknessMm = 1, Outward = true });
        Assert.Equal(9000, shell.Change.VolumeAfterMm3, 6);
        Assert.Empty(doc.Shells.Single().Faces);
    }

    [Fact]
    public void Requests_that_cannot_shell_name_their_field()
    {
        var doc = Part();
        Assert.Equal("faces_to_remove", Fails(() => Shell(new FeatureShellRequest { ThicknessMm = 2 })).Error.Details!["field"]);
        Block();
        var top = Face(doc);
        Assert.Equal("thickness_mm", Fails(() => Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top } })).Error.Details!["field"]);
        Assert.Equal("/faces_to_remove/1", Fails(() => Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top, top }, ThicknessMm = 2 })).Error.Details!["field"]);
    }

    [Fact]
    public void A_face_cut_back_to_the_walls_rim_counts_as_opened()
    {
        var doc = Part();
        Block();
        var top = Face(doc);
        doc.ShellLeavesRim = true;
        var shell = Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top }, ThicknessMm = 2 });
        Assert.Equal(6000, shell.Change.VolumeAfterMm3, 6);
        Assert.Contains(shell.Checks, c => c == "the 1 faces named in faces_to_remove are gone or cut back to the walls' rim (/faces_to_remove/0: 800 -> 224 mm²)");
    }

    [Fact]
    public void A_face_left_closed_fails_the_check_and_the_shell_is_rolled_back()
    {
        var doc = Part();
        Block();
        var top = Face(doc);
        doc.FacesSurvive = true;
        var e = Fails(() => Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top }, ThicknessMm = 2 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("gone", e.Error.Details!["check"]);
        // What the face resolves to now, and the volume, so a live failure says which of the two it is.
        Assert.Contains("it resolves to plane face", e.Message);
        Assert.Contains("the volume 8000 -> 6000 mm³", e.Message);
        Assert.StartsWith("plane face", (string)e.Error.Details!["measured"]!);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Shell1");
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3, 6);
    }
}

public class FeatureDraftTests : FeatureTestBase
{
    internal FeatureResult Draft(FeatureDraftRequest request) => Run(new FeatureDraftOperation(Services), request);

    /// <summary>A side face of the block, standing square to the front plane (its normal along +x).</summary>
    internal string Side(FakeDocument doc) =>
        Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, AreaMm2 = 200, Normal = new Vector3(1, 0, 0), Feature = "Boss-Extrude1" })).Reference!;

    [Fact]
    public void A_drafted_face_stands_at_ninety_less_the_angle_to_the_neutral_normal()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        var draft = Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side }, AngleDeg = 5 });
        Assert.Equal("Draft1", draft.Feature.Name);
        Assert.Contains(draft.Checks, c => c == "1 drafted planar faces stand at 85° to the neutral plane's normal");
        var spec = doc.Drafts.Single();
        Assert.Equal((5.0, false, 1), (spec.AngleDeg, spec.Reverse, spec.Faces.Count));
        Assert.Equal("Ebene vorne", doc.Describe(spec.Neutral).Name);
    }

    [Fact]
    public void A_draft_that_owns_no_faces_is_checked_on_the_faces_it_tilted()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        doc.DraftOwnsNoFaces = true;
        var draft = Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side }, AngleDeg = 5 });
        Assert.Contains(draft.Checks, c => c == "1 drafted planar faces stand at 85° to the neutral plane's normal");
    }

    [Fact]
    public void A_draft_with_no_face_left_to_measure_is_rolled_back()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        doc.DraftOwnsNoFaces = true;
        doc.DraftRemakesFaces = true;
        var e = Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side }, AngleDeg = 5 }));
        Assert.Equal("draft_angle", e.Error.Details!["check"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Draft1");
    }

    [Fact]
    public void A_face_made_anew_is_measured_from_the_drafts_own_faces()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        doc.DraftRemakesFaces = true;
        var draft = Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side }, AngleDeg = 3, Reverse = true });
        Assert.Contains(draft.Checks, c => c == "1 drafted planar faces stand at 87° to the neutral plane's normal");
        Assert.True(doc.Drafts.Single().Reverse);
    }

    [Fact]
    public void A_face_tilted_by_another_angle_is_rolled_back_and_put_back_as_it_was()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        doc.DraftAngleError = 1;
        var e = Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side }, AngleDeg = 5 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("draft_angle", e.Error.Details!["check"]);
        Assert.Equal(85.0, (double)e.Error.Details["expected"]!, 9);
        Assert.Equal(84.0, (double)e.Error.Details["measured"]!, 6);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Draft1");
        Assert.Equal(new Vector3(1, 0, 0).X, doc.Entities.Last(x => x.Kind == ReferenceKind.Face).Info.Normal!.X);
    }

    [Fact]
    public void Requests_that_cannot_draft_name_their_field()
    {
        var doc = Part();
        Block();
        var side = Side(doc);
        var cylinder = Services.References.Describe(doc, doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Feature = "Boss-Extrude1" })).Reference!;
        Assert.Equal("angle_deg", Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = "front", Faces = new List<string> { side } })).Error.Details!["field"]);
        Assert.Equal("faces", Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = "front", AngleDeg = 5 })).Error.Details!["field"]);
        Assert.Equal("neutral_plane", Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = cylinder, Faces = new List<string> { side }, AngleDeg = 5 })).Error.Details!["field"]);
        Assert.Equal("/faces/0", Fails(() => Draft(new FeatureDraftRequest { NeutralPlane = side, Faces = new List<string> { side }, AngleDeg = 5 })).Error.Details!["field"]);
    }
}

public class FeatureHoleTests : FeatureTestBase
{
    internal FeatureResult Hole(FeatureHoleRequest request) => Run(new FeatureHoleOperation(Services), request);

    /// <summary>The block's top face at z = 10, as model.find would name it: planar, normal +z, and every point asked about on it.</summary>
    internal (FakeEntity Face, string Reference) Top(FakeDocument doc)
    {
        var top = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, AreaMm2 = 800, Normal = new Vector3(0, 0, 1), DistanceMm = 0, Feature = "Boss-Extrude1" });
        return (top, Services.References.Describe(doc, top).Reference!);
    }

    static List<Point3> Two() => new List<Point3> { new Point3(10, 10, 10), new Point3(30, 10, 10) };

    [Fact]
    public void Two_simple_holes_are_drilled_at_their_points_and_checked_on_their_axes()
    {
        var doc = Part();
        Block();
        var (face, top) = Top(doc);
        var hole = Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 });
        Assert.Equal("Hole1", hole.Feature.Name);
        Assert.Equal(8000 - 2 * Math.PI * 6.25 * 6, hole.Change.VolumeAfterMm3, 6);
        Assert.Contains(hole.Checks, c => c == "2 holes, one on each position's axis");
        Assert.Equal(new[] { "Sketch2", "Hole1" }, hole.Change.Created.Select(c => c.Name).ToArray());
        var spec = doc.Holes.Single();
        Assert.Same(face, spec.Face);
        Assert.Equal((HoleType.Simple, HoleStandard.Iso, "Ø5.0", HoleEnd.Blind, 6.0), (spec.Type, spec.Standard, spec.Size, spec.End, spec.DepthMm));
        Assert.Equal(2, spec.Points.Count);
        var sketch = doc.Sketches.Single(s => s.Name == "Sketch2");
        Assert.Same(face, sketch.On);
        Assert.Equal(new[] { (10.0, 10.0), (30.0, 10.0) }, sketch.Points.Select(p => (p.At.X, p.At.Y)).ToArray());
        Assert.True(doc.Editors.Last().Closed);
    }

    [Fact]
    public void What_placing_the_holes_took_is_reported_in_the_checks()
    {
        var doc = Part();
        Block();
        var (_, top) = Top(doc);
        Assert.DoesNotContain(Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 }).Checks, c => c.StartsWith("placed with"));
        doc.HoleHow = "the sketch points (mark 0): 2 selected, HoleWizard5 returned a feature";
        var hole = Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 2 });
        Assert.Contains(hole.Checks, c => c == "placed with the sketch points (mark 0): 2 selected, HoleWizard5 returned a feature");
    }

    [Fact]
    public void A_tapped_hole_through_all_takes_no_depth()
    {
        var doc = Part();
        Block();
        var (_, top) = Top(doc);
        Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Type = HoleType.Tapped, Standard = HoleStandard.AnsiMetric, Size = " M5 ", End = HoleEnd.ThroughAll });
        var spec = doc.Holes.Single();
        Assert.Equal((HoleType.Tapped, HoleStandard.AnsiMetric, "M5", HoleEnd.ThroughAll, 0.0), (spec.Type, spec.Standard, spec.Size, spec.End, spec.DepthMm));
    }

    [Fact]
    public void A_hole_off_its_position_is_rolled_back_with_its_sketch()
    {
        var doc = Part();
        Block();
        var (_, top) = Top(doc);
        doc.HoleOffsetMm = 0.5;
        var e = Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("hole_position", e.Error.Details!["check"]);
        Assert.Equal("/positions/0", e.Error.Details["field"]);
        Assert.Equal(0.5, (double)e.Error.Details["measured"]!, 9);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Hole1" || f.Name == "Sketch2");
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3, 6);
    }

    [Fact]
    public void Fewer_holes_than_positions_fail_the_check()
    {
        var doc = Part();
        Block();
        var (_, top) = Top(doc);
        doc.HoleSkipsOne = true;
        var e = Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 }));
        Assert.Equal("hole_position", e.Error.Details!["check"]);
        Assert.Equal("/positions/1", e.Error.Details["field"]);
    }

    [Fact]
    public void Requests_that_cannot_be_drilled_name_their_field()
    {
        var doc = Part();
        var (face, top) = Top(doc);
        Assert.Equal("face", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 })).Error.Details!["field"]);
        Block();
        Assert.Equal("positions", Fails(() => Hole(new FeatureHoleRequest { Face = top, Size = "Ø5.0", DepthMm = 6 })).Error.Details!["field"]);
        Assert.Equal("/positions/1", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = new List<Point3> { new Point3(1, 1, 10), new Point3(1, 1, 10) }, Size = "Ø5.0", DepthMm = 6 })).Error.Details!["field"]);
        Assert.Equal("size", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = " ", DepthMm = 6 })).Error.Details!["field"]);
        Assert.Equal("depth_mm", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "M5" })).Error.Details!["field"]);
        Assert.Equal("depth_mm", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "M5", End = HoleEnd.ThroughAll, DepthMm = 6 })).Error.Details!["field"]);
        face.Info.DistanceMm = 0.5;
        Assert.Equal("/positions/0", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 })).Error.Details!["field"]);
        face.Info.Geometry = GeometryType.Cylinder;
        Assert.Equal("face", Fails(() => Hole(new FeatureHoleRequest { Face = top, Positions = Two(), Size = "Ø5.0", DepthMm = 6 })).Error.Details!["field"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sketch2");
    }
}
