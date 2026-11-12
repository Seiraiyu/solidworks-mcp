using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class FreedomTests
{
    static readonly Transform At = new Transform { Origin = new Point3(20, 10, 0) };

    static RemainingDofs Restricted(int freeT, int freeR)
    {
        var d = new RemainingDofs { Result = Freedom.Restricted };
        var dirs = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        for (int i = 0; i < 2; i++) d.Translations.Add(new DofSlot(i < freeT ? 3 : 1, dirs[i]));
        for (int i = 0; i < 2; i++) d.Rotations.Add(new DofSlot(i < freeR ? 4 : 0, new Vector3(0, 0, 1), new Point3(20, 10, 0)));
        return d;
    }

    [Theory]
    [InlineData(Constraint.Fixed)]
    [InlineData(Constraint.FullyDefined)]
    public void Held_components_have_no_free_motion(Constraint c)
    {
        var f = Freedom.Of("plate-1", c, 2, At, null);
        Assert.Equal(0, f.FreeTranslations);
        Assert.Equal(0, f.FreeRotations);
    }

    [Fact]
    public void An_unmated_floating_component_is_free_in_all_six()
    {
        var f = Freedom.Of("pin-1", Constraint.UnderDefined, 0, At, null);
        Assert.Equal(3, f.FreeTranslations);
        Assert.Equal(3, f.FreeRotations);
        Assert.Equal(20, f.Rotations[0].Through.X);
    }

    [Fact]
    public void One_coincident_face_leaves_two_slides_and_one_turn()
    {
        var f = Freedom.Of("block-1", Constraint.UnderDefined, 1, At, Restricted(2, 1));
        Assert.Equal(2, f.FreeTranslations);
        Assert.Equal(1, f.FreeRotations);
        Assert.Equal(1, f.Rotations[0].Axis.Z);
        Assert.Null(f.Note);
    }

    [Fact]
    public void A_concentric_pin_slides_and_turns_on_its_axis()
    {
        var f = Freedom.Of("pin-1", Constraint.UnderDefined, 1, At, Restricted(1, 1));
        Assert.Equal(1, f.FreeTranslations);
        Assert.Equal(1, f.FreeRotations);
    }

    [Theory]
    [InlineData(Freedom.Unrestricted, "more ways")]
    [InlineData(Freedom.Unavailable, "constrained state only")]
    [InlineData(Freedom.Failed, "swRemainingDofs_Failed")]
    public void Answers_other_than_restricted_leave_the_counts_absent(int result, string note)
    {
        var f = Freedom.Of("pin-1", Constraint.UnderDefined, 1, At, new RemainingDofs { Result = result });
        Assert.Null(f.FreeTranslations);
        Assert.Null(f.FreeRotations);
        Assert.Contains(note, f.Note);
    }

    [Fact]
    public void No_answer_and_no_free_slot_are_both_said()
    {
        Assert.Contains("GetRemainingDOFs", Freedom.Of("pin-1", Constraint.UnderDefined, 1, At, null).Note);
        Assert.Contains("no free motion", Freedom.Of("pin-1", Constraint.UnderDefined, 1, At, Restricted(0, 0)).Note);
    }

    [Fact]
    public void Over_defined_says_its_mates_conflict()
    {
        var f = Freedom.Of("pin-1", Constraint.OverDefined, 3, At, null);
        Assert.Null(f.FreeTranslations);
        Assert.Contains("conflict", f.Note);
    }
}
