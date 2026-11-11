using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every error code each 6.C operation lists in the plan's code table is reachable (design 454, Error handling), one case
/// per code. The dispatcher's own busy (not started), timeout and dialog_open, which every operation can answer, are
/// proven by phase 2's tests.
/// </summary>
public class SheetMetalBendErrorCodeTests : SheetMetalBendTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sheetmetal_sketched_bend_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => SketchedBend(new SheetMetalSketchedBendRequest { Sketch = "Sketch1", FixedFace = "r1.face.x", AngleDeg = 90 })).Code);
            return;
        }
        var doc = Tabbed();
        var request = new SheetMetalSketchedBendRequest { Sketch = Lines(doc, 30).Sketch.Name, FixedFace = Base(doc), AngleDeg = 90 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.AngleDeg = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.SheetFaceList[0].State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.BendVolumeError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => SketchedBend(request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "SM3dBend");
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
    }

    [Theory]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Sheetmetal_flatten_reaches_every_listed_code(ErrorCode code)
    {
        SheetMetalFlattenResult Flatten() => Run(new SheetMetalFlattenOperation(Services), new SheetMetalFlattenRequest { Flat = true });
        if (code == ErrorCode.WrongDocumentType)
        {
            Part();
            Block();
            Assert.Equal(code, Fails(() => Flatten()).Code);
            return;
        }
        var doc = Tabbed();
        switch (code)
        {
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.SuppressRefused = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FlattenVolumeError = 1;
                break;
        }
        Assert.Equal(code, Fails(() => Flatten()).Code);
        Assert.Equal(SheetState.Folded, SheetMetalParts.State(doc));
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sheetmetal_unfold_and_fold_reach_every_listed_code(ErrorCode code)
    {
        SheetMetalFeatureResult Unfold(SheetMetalUnfoldRequest r) => Run(new SheetMetalUnfoldOperation(Services), r);
        SheetMetalFeatureResult Fold(SheetMetalFoldRequest r) => Run(new SheetMetalFoldOperation(Services), r);
        if (code == ErrorCode.WrongDocumentType)
        {
            Part();
            Block();
            Assert.Equal(code, Fails(() => Unfold(new SheetMetalUnfoldRequest { FixedFace = "r1.face.x" })).Code);
            Assert.Equal(code, Fails(() => Fold(new SheetMetalFoldRequest())).Code);
            return;
        }
        var doc = Tabbed();
        EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25 });
        var unfold = new SheetMetalUnfoldRequest { FixedFace = Base(doc) };
        double folded = doc.MassProperties!.VolumeMm3;
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                unfold.Bends = new List<string> { "Base-Flange1" };
                Assert.Equal(code, Fails(() => Fold(new SheetMetalFoldRequest())).Code);
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.SheetFaceList[0].State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                Assert.Equal(code, Fails(() => Fold(new SheetMetalFoldRequest())).Code);
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FoldsDoNothing = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Unfold(unfold)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == SheetMetalParts.UnfoldType);
        Assert.Equal(folded, doc.MassProperties.VolumeMm3, 9);
    }

    [Fact]
    public void A_fold_SOLIDWORKS_does_not_make_fails_naming_the_turning_side_limit()
    {
        // 6.C's live run: SOLIDWORKS 2026 SP4 makes no Fold once a cut reaches past the bend into the side that turns.
        var doc = Tabbed();
        EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Top(doc) }, LengthMm = 25 });
        Run(new SheetMetalUnfoldOperation(Services), new SheetMetalUnfoldRequest { FixedFace = Base(doc) });
        doc.FeatureRefuses = true;
        var e = Fails(() => Run(new SheetMetalFoldOperation(Services), new SheetMetalFoldRequest()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("past the bend into the side that turns", e.Message);
        Assert.DoesNotContain(doc.Tree, f => f.Type == SheetMetalParts.FoldType);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sheetmetal_convert_reaches_every_listed_code(ErrorCode code)
    {
        SheetMetalFeatureResult Convert(SheetMetalConvertRequest r) => Run(new SheetMetalConvertOperation(Services), r);
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Convert(new SheetMetalConvertRequest { FixedFace = "r1.face.x", BendEdges = new List<string> { "r1.edge.x" }, ThicknessMm = 2, BendRadiusMm = 3 })).Code);
            return;
        }
        var doc = Part();
        Block();
        doc.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 50) };
        var floor = doc.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, Normal = new Vector3(0, 1, 0), Origin = new Point3(31, 2, 25), DistanceMm = 0 });
        var wall = doc.Add(ReferenceKind.Face, new EntityInfo { Feature = "Boss-Extrude1", Geometry = GeometryType.Plane, Normal = new Vector3(1, 0, 0), Origin = new Point3(2, 21, 25), DistanceMm = 0 });
        var edge = doc.Corner(floor, wall, new Point3(2, 2, 25), new Vector3(0, 0, 1), 50);
        var request = new SheetMetalConvertRequest { FixedFace = Ref(doc, floor), BendEdges = new List<string> { Ref(doc, edge) }, ThicknessMm = 2, BendRadiusMm = 3 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.ThicknessMm = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                edge.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.ConvertBoxGrowth = 5;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Convert(request)).Code);
        Assert.False(SheetMetalParts.IsSheetMetal(doc));
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3, 9);
    }
}
