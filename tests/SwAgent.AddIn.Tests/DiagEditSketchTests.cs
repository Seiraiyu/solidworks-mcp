using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DiagEditSketchTests : FeatureTestBase
{
    AddInServices Diagnostic => new AddInServices(Sw, "0.1.0-test", new AgentRuntime("0.1.0-test", 1, NullLog.Instance, diagnosticsEnabled: true));

    DiagEditingResult Edit(AddInServices services, DiagEditSketchRequest request) => Run(new DiagEditSketchOperation(services), request);

    [Fact]
    public void A_sketch_opened_for_editing_makes_changes_wait_until_it_is_exited()
    {
        var doc = Part();
        var services = Diagnostic;
        Assert.Equal("Sketch1", Edit(services, new DiagEditSketchRequest { Sketch = "Sketch1", Open = true }).Sketch);
        var busy = Fails(() => Block());
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("Sketch1", busy.Error.Details!["sketch"]);
        Assert.Null(Edit(services, new DiagEditSketchRequest { Open = false }).Sketch);
        Assert.Equal("Boss-Extrude1", Block().Feature.Name);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Diag_edit_sketch_reaches_every_listed_code(ErrorCode code)
    {
        var services = Diagnostic;
        var request = new DiagEditSketchRequest { Sketch = "Sketch1", Open = true };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Edit(services, request)).Code);
            return;
        }
        var doc = Part();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                services = Services;
                break;
            case ErrorCode.ReferenceUnresolved:
                var sketch = doc.Entities.Find(e => e.Info.Name == "Sketch1")!;
                request.Sketch = Services.References.Describe(doc, sketch).Reference;
                sketch.State = PersistState.Deleted;
                break;
            case ErrorCode.VerificationFailed:
                doc.EditingSketchName = "Sketch1";
                request = new DiagEditSketchRequest { Open = false };
                doc.ExitIgnored = true;
                break;
            case ErrorCode.ApiError:
                doc.OpensForEditing = false;
                break;
        }
        Assert.Equal(code, Fails(() => Edit(services, request)).Code);
    }
}
