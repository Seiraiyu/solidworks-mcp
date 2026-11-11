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
}
