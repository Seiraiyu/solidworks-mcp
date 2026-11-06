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
