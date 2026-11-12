using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ComponentLinearPatternTests : ComponentPatternTestBase
{
    [Fact]
    public void A_row_puts_each_instance_spacing_further_along_and_says_so()
    {
        var r = BlockRow();
        Assert.Equal("LocalLPattern1", r.Pattern.Name);
        Assert.Equal(ComponentPatternKind.Linear, r.Pattern.Kind);
        Assert.StartsWith("r1.feature.", r.Pattern.Reference);
        Assert.Equal(new[] { "block-1" }, r.Pattern.Seeds);
        Assert.Equal(2, r.Pattern.Instances.Count);
        Assert.Equal("along", r.Sense);
        Assert.Equal(new[] { 20.0, 40.0 }, r.Instances.Select(i => i.Origin.X).OrderBy(x => x));
        Assert.All(r.Instances, i => Assert.Equal(0, i.OffsetMm, 9));
        Assert.Equal(ReferenceKind.Feature, Assert.Single(r.Change.Created).Kind);
        Assert.Equal(r.Change.ComponentsBefore + 2, r.Change.ComponentsAfter);
        Assert.Contains(r.Change.Constraints, c => c.Name == r.Pattern.Instances[0]);
    }

    [Fact]
    public void A_grid_fills_both_directions()
    {
        var r = Linear(new ComponentLinearPatternRequest
        {
            Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = 20, Count = 3,
            Direction2 = new ComponentPatternDirection { Direction = Ref(PlateFront), SpacingMm = 15, Count = 2 },
        });
        Assert.Equal(5, r.Instances.Count);
        Assert.Equal("along/along", r.Sense);
        Assert.Contains(r.Instances, i => i.Origin.X == 40 && i.Origin.Y == -55);
    }

    [Fact]
    public void Reversed_it_goes_the_other_way_and_still_holds()
    {
        var r = Linear(new ComponentLinearPatternRequest { Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = 20, Count = 3, Reverse = true });
        Assert.Equal("against", r.Sense);
        Assert.Contains(r.Instances, i => i.Origin.X == -40);
    }

    [Fact]
    public void An_instance_off_by_half_a_millimetre_rolls_the_pattern_back()
    {
        Asm.PlaceInstance = (seed, i) => At(20 * i + 0.5, -40, 0);
        var e = Fails(BlockRowAction);
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("instance_offset_mm", e.Error.Details!["check"]);
        Assert.Empty(Asm.PatternItems);
        Assert.Equal(new[] { "plate-1", "pin-1", "block-1" }, Asm.Items.Select(c => c.Info.Name));
        Assert.Contains("delete LocalLPattern1", Asm.Calls);
    }

    void BlockRowAction() => BlockRow();

    [Theory]
    [InlineData(null, 20.0, "/count")]
    [InlineData(501, 20.0, "/count")]
    [InlineData(3, null, "/spacing_mm")]
    [InlineData(3, 0.0, "/spacing_mm")]
    public void Counts_and_spacings_are_checked_before_SOLIDWORKS_is_asked(int? count, double? spacing, string field)
    {
        var e = Fails(() => Linear(new ComponentLinearPatternRequest { Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = spacing, Count = count }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void A_cylinder_is_no_direction()
    {
        var e = Fails(() => Linear(new ComponentLinearPatternRequest { Components = { "block-1" }, Direction = Ref(PlateCentre), SpacingMm = 20, Count = 3 }));
        Assert.Equal("/direction", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_second_direction_parallel_to_the_first_is_refused()
    {
        var e = Fails(() => Linear(new ComponentLinearPatternRequest
        {
            Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = 20, Count = 3,
            Direction2 = new ComponentPatternDirection { Direction = Ref(PlateEdge), SpacingMm = 15, Count = 2 },
        }));
        Assert.Equal("/direction2/direction", e.Error.Details!["field"]);
    }

    [Fact]
    public void Operation_failed_says_how_each_way_of_asking_went()
    {
        Asm.PatternFails = true;
        var e = Fails(BlockRowAction);
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("fake: returned null", e.Error.Details!["tried"]);
    }
}
