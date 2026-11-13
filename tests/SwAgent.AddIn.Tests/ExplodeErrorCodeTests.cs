using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ExplodeErrorCodeTests : ExplodeTestBase
{
    [Fact]
    public void Wrong_document_type_for_every_new_operation_on_a_part()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        var create = Request(Step("z", 10, "x"));
        create.Document = "block.SLDPRT";
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Explode(create)).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ExplodeListOperation(Services), new ExplodeListRequest { Document = "block.SLDPRT" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ExplodeShowOperation(Services), new ExplodeShowRequest { Document = "block.SLDPRT", View = "v" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ExplodeDeleteOperation(Services), new ExplodeDeleteRequest { Document = "block.SLDPRT", View = "v" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ExportAssemblyOperation(Services), new ExportAssemblyRequest { Document = "block.SLDPRT", Path = PathOf("b.step") })).Code);
    }

    [Fact]
    public void Busy_while_a_component_is_edited_in_context()
    {
        Asm.EditingTitle = "base.SLDPRT";
        var e = Fails(() => Explode(Step("z", 40, "pin-1")));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Reference_unresolved_when_the_directions_component_is_suppressed()
    {
        string edge = Ref(PlateEdge);
        Plate.Info.State = ComponentState.Suppressed;
        PlateEdge.State = PersistState.Suppressed;
        var e = Fails(() => Explode(Step(edge, 10, "pin-1")));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("component_suppressed", e.Error.Details!["reason"]);
        Assert.Equal("/steps/0/direction", e.Error.Details["field"]);
    }

    [Fact]
    public void Invalid_argument_names_the_nested_field()
    {
        var e = Fails(() => Explode(Step("z", 10, "pin-1"), Step("x", 20000, "bracket-1")));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/steps/1/distance_mm", e.Error.Details!["field"]);
    }

    [Fact]
    public void Operation_failed_and_verification_failed_are_reached()
    {
        Asm.StepFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Explode(Step("z", 10, "pin-1"))).Code);
        Asm.StepFails = false;
        Asm.StepStrays = (c, i) => new Vector3(0, 0.01, 0);
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => Explode(Step("z", 10, "pin-1"))).Code);
    }
}
