using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every error code each 3.D operation lists in the plan's code table is reachable (design 371, Error handling), one
/// case per code, as ErrorCodeTests does for 3.A to 3.C. The dispatcher's own busy (not started), timeout and
/// dialog_open, which every operation can answer, are proven by phase 2's tests.
/// </summary>
public class MoreErrorCodeTests : FeatureTestBase
{
    static SwAgentException Reach(System.Action action) => Fails(action);

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_sweep_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureSweepRequest { Profile = "Sketch1", Path = "Sketch2" };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Reach(() => Run(new FeatureSweepOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "path", Start = P(0, 0), End = P(0, -30) } } }, on: "top");
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Path = "Sketch1";
                break;
            case ErrorCode.ReferenceUnresolved:
                var edge = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 10 });
                request.Path = Services.References.Describe(doc, edge).Reference!;
                edge.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch2";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureVolumeError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureSweepOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Sweep");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_loft_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureLoftRequest { Profiles = new List<string> { "Sketch1", "Sketch2" } };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Reach(() => Run(new FeatureLoftOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        var small = Sketch(new SketchGeometry { Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "small", Corner1 = P(10, 5), Corner2 = P(30, 15) } } });
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Profiles.RemoveAt(1);
                break;
            case ErrorCode.ReferenceUnresolved:
                request.Profiles[1] = small.Sketch.Reference!;
                ((FakeEntity)doc.FeatureByName("Sketch2")!).State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Loft";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureAddsNothing = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureLoftOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Blend");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_rib_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureRibRequest { Sketch = "Sketch2", ThicknessMm = 2 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Reach(() => Run(new FeatureRibOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        Block();
        var line = Sketch(new SketchGeometry { Lines = new List<SketchLine> { new SketchLine { Id = "rib", Start = P(5, 10), End = P(35, 10) } } });
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.ThicknessMm = -1;
                break;
            case ErrorCode.ReferenceUnresolved:
                request.Sketch = line.Sketch.Reference!;
                ((FakeEntity)doc.FeatureByName("Sketch2")!).State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Rib";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureAddsNothing = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureRibOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Rib");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_shell_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureShellRequest { ThicknessMm = 2 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Reach(() => Run(new FeatureShellOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        Block();
        request.FacesToRemove = new List<string> { Face(doc) };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.ThicknessMm = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.Entities.Last(e => e.Kind == ReferenceKind.Face).State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FacesSurvive = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureShellOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Shell");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_draft_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureDraftRequest { NeutralPlane = "front", AngleDeg = 5 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            request.Faces.Add("Face1");
            Assert.Equal(code, Reach(() => Run(new FeatureDraftOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        Block();
        var side = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(1, 0, 0) });
        request.Faces.Add(Services.References.Describe(doc, side).Reference!);
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.AngleDeg = 90;
                break;
            case ErrorCode.ReferenceUnresolved:
                side.State = PersistState.Suppressed;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Draft";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.DraftAngleError = 2;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureDraftOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Draft");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_hole_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureHoleRequest { Face = "Face1", Positions = new List<Point3> { new Point3(20, 10, 10) }, Size = "Ø5.0", DepthMm = 5 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Reach(() => Run(new FeatureHoleOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        Block();
        var top = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1), DistanceMm = 0 });
        request.Face = Services.References.Describe(doc, top).Reference!;
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.DepthMm = null;
                break;
            case ErrorCode.ReferenceUnresolved:
                top.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Hole Specification";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.HoleOffsetMm = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Reach(() => Run(new FeatureHoleOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "HoleWzd" || f.Name == "Sketch2");
    }
}
