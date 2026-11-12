using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ComponentCircularPatternTests : ComponentPatternTestBase
{
    [Fact]
    public void Four_round_the_centre_hole_turn_origin_and_axes()
    {
        var r = Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 4 });
        Assert.Equal(ComponentPatternKind.Circular, r.Pattern.Kind);
        Assert.Equal(3, r.Instances.Count);
        Assert.Contains(r.Instances, i => System.Math.Abs(i.Origin.X - 40) < 1e-9 && System.Math.Abs(i.Origin.Y - 60) < 1e-9);
        Assert.All(r.Instances, i => Assert.Equal(0, i.TurnDeg!.Value, 6));
        Assert.Equal("along", r.Sense);
    }

    [Fact]
    public void Half_a_turn_in_three_is_ninety_degree_steps()
    {
        var r = Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 3, AngleDeg = 180 });
        Assert.Contains(r.Instances, i => System.Math.Abs(i.Origin.X - 20) < 1e-9 && System.Math.Abs(i.Origin.Y - 40) < 1e-9);
    }

    [Fact]
    public void Reversed_it_turns_the_other_way()
    {
        var r = Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 3, AngleDeg = 90, EqualSpacing = false, Reverse = true });
        Assert.Equal("against", r.Sense);
        Assert.Contains(r.Instances, i => System.Math.Abs(i.Origin.Y - 20) < 1e-9);
    }

    [Fact]
    public void Without_equal_spacing_the_step_is_needed()
    {
        var e = Fails(() => Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 3, EqualSpacing = false }));
        Assert.Equal("/angle_deg", e.Error.Details!["field"]);
    }

    [Fact]
    public void Steps_that_go_round_onto_the_seed_are_refused()
    {
        var e = Fails(() => Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 5, AngleDeg = 90, EqualSpacing = false }));
        Assert.Equal("/angle_deg", e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void A_plane_is_no_axis()
    {
        var e = Fails(() => Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateTop), Count = 4 }));
        Assert.Equal("/axis", e.Error.Details!["field"]);
    }

    [Fact]
    public void An_instance_left_unturned_rolls_back()
    {
        Asm.PlaceInstance = (seed, i) => new Transform
        {
            Origin = SwAgent.AddIn.Core.PatternGeometry.Turn(seed.Info.Transform, new Point3(40, 40, 0), new Vector3(0, 0, 1), 90 * i).Origin,
        };
        var e = Fails(() => Circular(new ComponentCircularPatternRequest { Components = { "pin-1" }, Axis = Ref(PlateCentre), Count = 4 }));
        Assert.Equal("instance_turn_deg", e.Error.Details!["check"]);
        Assert.Empty(Asm.PatternItems);
    }
}
