using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateErrorCodeTests : MateTestBase
{
    [Fact]
    public void Wrong_document_type_for_every_mate_operation_on_a_part()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new MateAddOperation(Services), new MateAddRequest { Document = "block.SLDPRT", Type = MateType.Lock })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new MateEditOperation(Services), new MateEditRequest { Document = "block.SLDPRT", Mate = "x", Suppressed = true })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new MateDeleteOperation(Services), new MateDeleteRequest { Document = "block.SLDPRT", Mates = { "x" } })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new AssemblyDofOperation(Services), new AssemblyDofRequest { Document = "block.SLDPRT" })).Code);
    }

    [Fact]
    public void Reference_unresolved_with_the_component_suppressed_reason()
    {
        string pinTop = Ref(PinTop);
        Pin.Info.State = ComponentState.Suppressed;
        PinTop.State = PersistState.Suppressed;
        var e = Fails(() => Add(MateType.Coincident, Side(pinTop), Side(Ref(PlateTop))));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("component_suppressed", e.Error.Details!["reason"]);
        Assert.Equal("/entities/0/entity", e.Error.Details["field"]);
    }

    [Fact]
    public void Busy_for_edit_and_delete_too()
    {
        Asm.OnMate = (_, _) => Move(Block, 0, 0, 10);
        Add(MateType.Coincident, Side(Ref(BlockBottom)), Side(Ref(PlateTop)));
        Asm.EditingTitle = "base.SLDPRT";
        Assert.Equal(ErrorCode.Busy, Fails(() => Run(new MateEditOperation(Services), new MateEditRequest { Document = "stand.SLDASM", Mate = "Coincident1", Suppressed = true })).Code);
        Assert.Equal(ErrorCode.Busy, Fails(() => Run(new MateDeleteOperation(Services), new MateDeleteRequest { Document = "stand.SLDASM", Mates = { "Coincident1" } })).Code);
    }

    [Fact]
    public void Operation_failed_when_SOLIDWORKS_will_not_suppress()
    {
        Asm.OnMate = (_, _) => Move(Block, 0, 0, 10);
        Add(MateType.Coincident, Side(Ref(BlockBottom)), Side(Ref(PlateTop)));
        Asm.MateSuppressionRefused = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Run(new MateEditOperation(Services), new MateEditRequest { Document = "stand.SLDASM", Mate = "Coincident1", Suppressed = true })).Code);
    }
}
