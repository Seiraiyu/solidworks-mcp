using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateEditTests : MateTestBase
{
    public MateEditTests()
    {
        Asm.OnMate = (_, _) => Move(Block, 15, 0, 0);
        Add(MateType.Distance, Side(Ref(BlockLeft)), Side(Ref(PlateLeft)), distance: 15);
        Asm.OnMate = null;
    }

    MateEditResult Edit(MateEditRequest r)
    {
        r.Document = "stand.SLDASM";
        return Run(new MateEditOperation(Services), r);
    }

    [Fact]
    public void A_new_distance_is_measured_and_reported_as_changed()
    {
        Asm.OnMate = (_, _) => Move(Block, 10, 0, 0);
        var r = Edit(new MateEditRequest { Mate = "Distance1", DistanceMm = 25 });
        Assert.Equal(25, r.Mate.DistanceMm);
        Assert.Equal(25, r.Measured.Single().Measured, 9);
        var changed = Assert.Single(r.Change.Changed);
        Assert.Equal("distance_mm", changed.Property);
        Assert.Equal("15", changed.Before);
        Assert.Equal("25", changed.After);
    }

    [Fact]
    public void A_value_that_does_not_hold_is_put_back_with_the_block()
    {
        Asm.OnMate = (_, m) => { if (m.Facts.DistanceMm == 25) Move(Block, 3, 0, 0); };
        var e = Fails(() => Edit(new MateEditRequest { Mate = "Distance1", DistanceMm = 25 }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(15, Asm.MateItems.Single().Facts.DistanceMm);
        Assert.Equal(15, Block.Info.Transform.Origin.X, 9);
    }

    [Fact]
    public void Suppressing_imposes_nothing_and_is_checked_by_its_state()
    {
        var r = Edit(new MateEditRequest { Mate = "Distance1", Suppressed = true });
        Assert.True(r.Mate.Suppressed);
        Assert.Empty(r.Measured);
        Assert.Equal("suppressed", Assert.Single(r.Change.Changed).Property);
    }

    [Theory]
    [InlineData(null, 30.0, null, "/angle_deg")]
    [InlineData(null, null, null, "/mate")]
    public void A_value_the_mate_does_not_have_is_refused(double? distance, double? angle, bool? suppressed, string field)
    {
        var e = Fails(() => Edit(new MateEditRequest { Mate = "Distance1", DistanceMm = distance, AngleDeg = angle, Suppressed = suppressed }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }

    [Fact]
    public void An_advanced_mate_is_left_to_api_call()
    {
        Asm.MateItems.Single().Facts.Type = null;
        Asm.MateItems.Single().Facts.SolidWorksType = "swMateWIDTH";
        var e = Fails(() => Edit(new MateEditRequest { Mate = "Distance1", Suppressed = true }));
        Assert.Contains("api.call", e.Message);
    }

    [Fact]
    public void SOLIDWORKS_refusing_the_edit_is_operation_failed()
    {
        Asm.EditMateRefused = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Edit(new MateEditRequest { Mate = "Distance1", DistanceMm = 20 })).Code);
    }
}
