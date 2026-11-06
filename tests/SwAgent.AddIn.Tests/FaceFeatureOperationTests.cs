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
    public void A_face_left_closed_fails_the_check_and_the_shell_is_rolled_back()
    {
        var doc = Part();
        Block();
        var top = Face(doc);
        doc.FacesSurvive = true;
        var e = Fails(() => Shell(new FeatureShellRequest { FacesToRemove = new List<string> { top }, ThicknessMm = 2 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("gone", e.Error.Details!["check"]);
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
