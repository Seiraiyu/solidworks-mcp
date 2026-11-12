using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateDeleteTests : MateTestBase
{
    readonly string _reference;

    public MateDeleteTests()
    {
        Asm.OnMate = (_, _) => Move(Block, 0, 0, 10);
        _reference = Add(MateType.Coincident, Side(Ref(BlockBottom)), Side(Ref(PlateTop))).Mate.Reference!;
        Asm.OnMate = null;
    }

    MateDeleteResult Delete(params string[] mates) =>
        Run(new MateDeleteOperation(Services), new MateDeleteRequest { Document = "stand.SLDASM", Mates = new System.Collections.Generic.List<string>(mates) });

    [Fact]
    public void Deleted_mates_are_gone_and_their_components_stay_put()
    {
        var r = Delete(_reference);
        Assert.Equal(new[] { "Coincident1" }, r.Deleted);
        Assert.Equal(new[] { "block-1", "plate-1" }, r.Stayed);
        Assert.Equal(0, r.Change.MatesAfter);
        Assert.Empty(r.Change.Moved);
        Assert.Equal(new[] { "block-1", "plate-1" }, r.Change.Constraints.Select(c => c.Name).OrderBy(n => n));
        Assert.Equal(10, Block.Info.Transform.Origin.Z, 9);
    }

    [Fact]
    public void A_deleted_mates_reference_is_stale()
    {
        Delete("Coincident1");
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Delete(_reference)).Code);
    }

    [Fact]
    public void A_component_deleted_takes_its_mates_and_their_references_go_stale()
    {
        Run(new ComponentDeleteOperation(Services), new ComponentDeleteRequest { Document = "stand.SLDASM", Components = { "block-1" } });
        Assert.Empty(Asm.MateItems);
        var e = Fails(() => Run(new MateEditOperation(Services), new MateEditRequest { Document = "stand.SLDASM", Mate = _reference, Suppressed = true }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("/mate", e.Error.Details!["field"]);
    }

    [Fact]
    public void An_unknown_mate_names_its_place_in_the_list()
    {
        var e = Fails(() => Delete("Coincident1", "Nope1"));
        Assert.Equal("/mates/1", e.Error.Details!["field"]);
        Assert.Single(Asm.MateItems);
    }

    [Fact]
    public void An_empty_list_is_refused()
    {
        Assert.Equal("/mates", Fails(() => Delete()).Error.Details!["field"]);
    }
}
