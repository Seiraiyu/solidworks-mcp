using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every error code each 6.A operation lists in the plan's code table is reachable (design 454, Error handling), one case
/// per code. The dispatcher's own busy (not started), timeout and dialog_open, which every operation can answer, are
/// proven by phase 2's tests.
/// </summary>
public class SheetMetalErrorCodeTests : SheetMetalTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sheetmetal_base_flange_reaches_every_listed_code(ErrorCode code)
    {
        var request = new SheetMetalBaseFlangeRequest { Sketch = "Sketch1", ThicknessMm = 2, BendRadiusMm = 3 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => BaseFlange(request)).Code);
            return;
        }
        var doc = Part();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.ThicknessMm = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                var sketch = (FakeEntity)doc.FeatureByName("Sketch1")!;
                request.Sketch = Services.References.Describe(doc, sketch).Reference!;
                sketch.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.ThicknessError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => BaseFlange(request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "SheetMetal");
    }
}

public class SheetMetalReadErrorCodeTests : SheetMetalTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.WrongDocumentType)]
    public void Sheetmetal_read_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType) Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
        else Part();
        var request = new SheetMetalReadRequest { Document = code == ErrorCode.InvalidArgument ? "Nope" : null };
        Assert.Equal(code, Fails(() => Run(new SheetMetalReadOperation(Services), request)).Code);
    }
}

public class SheetMetalSetParametersErrorCodeTests : SheetMetalTestBase
{
    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Sheetmetal_set_parameters_reaches_every_listed_code(ErrorCode code)
    {
        var request = new SheetMetalSetParametersRequest { ThicknessMm = 3 };
        var doc = Part();
        if (code != ErrorCode.WrongDocumentType) Tab();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.ThicknessMm = -1;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.VerificationFailed:
                doc.DefaultsIgnored = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new SheetMetalSetParametersOperation(Services), request)).Code);
        Assert.NotEqual(3, doc.Sheet?.ThicknessMm);
    }
}
