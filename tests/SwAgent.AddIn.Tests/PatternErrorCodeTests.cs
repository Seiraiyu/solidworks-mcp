using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class PatternErrorCodeTests : ComponentPatternTestBase
{
    [Fact]
    public void Wrong_document_type_for_every_new_operation_on_a_part()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ComponentLinearPatternOperation(Services), new ComponentLinearPatternRequest { Document = "block.SLDPRT", Components = { "x" }, Direction = "front" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ComponentCircularPatternOperation(Services), new ComponentCircularPatternRequest { Document = "block.SLDPRT", Components = { "x" }, Axis = "x" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ComponentFeaturePatternOperation(Services), new ComponentFeaturePatternRequest { Document = "block.SLDPRT", Components = { "x" }, DrivingFeature = "x" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new AssemblyInterferenceOperation(Services), new AssemblyInterferenceRequest { Document = "block.SLDPRT" })).Code);
    }

    [Fact]
    public void Busy_while_a_component_is_edited_in_context()
    {
        Asm.EditingTitle = "base.SLDPRT";
        var e = Fails(() => BlockRow());
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Reference_unresolved_when_the_direction_s_component_is_suppressed()
    {
        string edge = Ref(PlateEdge);
        Plate.Info.State = ComponentState.Suppressed;
        PlateEdge.State = PersistState.Suppressed;
        var e = Fails(() => Linear(new ComponentLinearPatternRequest { Components = { "block-1" }, Direction = edge, SpacingMm = 20, Count = 3 }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("component_suppressed", e.Error.Details!["reason"]);
        Assert.Equal("/direction", e.Error.Details["field"]);
    }

    [Fact]
    public void Invalid_argument_names_the_nested_field()
    {
        var e = Fails(() => Linear(new ComponentLinearPatternRequest
        {
            Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = 20, Count = 3,
            Direction2 = new ComponentPatternDirection { Direction = Ref(PlateFront), SpacingMm = 15 },
        }));
        Assert.Equal("/direction2/count", e.Error.Details!["field"]);
    }

    [Fact]
    public void Operation_failed_for_every_pattern_kind_when_SOLIDWORKS_makes_none()
    {
        Asm.PatternFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 4 })).Code);
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "CirPattern1@plate-1" })).Code);
    }

    [Fact]
    public void Verification_failed_when_the_pattern_has_a_rebuild_error()
    {
        Asm.OnSolve = a => { foreach (var p in a.PatternItems) p.Error = "swFeatureErrorComponentPatternFailed"; };
        var e = Fails(() => BlockRow());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("pattern_error", e.Error.Details!["check"]);
        Assert.Empty(Asm.PatternItems);
    }
}
