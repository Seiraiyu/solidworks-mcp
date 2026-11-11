using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every error code each 6.B operation lists in the plan's code table is reachable (design 454, Error handling), one case
/// per code. The dispatcher's own busy (not started), timeout and dialog_open, which every operation can answer, are
/// proven by phase 2's tests.
/// </summary>
public class SheetMetalFlangeErrorCodeTests : SheetMetalFlangeTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sheetmetal_edge_flange_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Edges = new List<string> { "r1.edge.x" }, LengthMm = 25 })).Code);
            return;
        }
        var doc = Sheet();
        var edge = doc.SheetEdge(new Point3(0, 20, 0), new Point3(40, 20, 0), new Vector3(0, 1, 0));
        var request = new SheetMetalEdgeFlangeRequest { Edges = new List<string> { Ref(doc, edge) }, LengthMm = 25 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.LengthMm = 0;
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
                doc.FeatureVolumeError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => EdgeFlange(request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "EdgeFlange");
        Assert.Equal(1600, doc.MassProperties!.VolumeMm3, 9);
    }
}
