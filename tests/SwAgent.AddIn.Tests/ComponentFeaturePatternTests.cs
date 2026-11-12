using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ComponentFeaturePatternTests : ComponentPatternTestBase
{
    static readonly Point3[] OtherHoles = { new Point3(40, 60, 0), new Point3(20, 40, 0), new Point3(40, 20, 0) };

    public ComponentFeaturePatternTests()
    {
        Asm.PlaceInstance = (seed, i) => new Transform { Origin = OtherHoles[i - 1] };
    }

    [Fact]
    public void One_pin_becomes_one_in_every_hole()
    {
        var r = Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "CirPattern1@plate-1" });
        Assert.Equal(ComponentPatternKind.Feature, r.Pattern.Kind);
        Assert.Equal(4, r.Holes);
        Assert.Equal(3, r.Instances.Count);
        Assert.All(r.Instances, i => Assert.Null(i.TurnDeg));
        Assert.Null(r.Sense);
        Assert.Contains("feature pattern CirPattern1", Asm.Calls);
    }

    [Fact]
    public void A_feature_that_makes_no_holes_is_refused_before_SOLIDWORKS_is_asked()
    {
        var e = Fails(() => Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "Boss-Extrude1@plate-1" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/driving_feature", e.Error.Details!["field"]);
        Assert.Equal(0, e.Error.Details["holes"]);
        Assert.DoesNotContain(Asm.Calls, c => c.StartsWith("feature pattern"));
    }

    [Fact]
    public void A_seed_that_is_not_on_a_hole_is_refused()
    {
        Pin.Info.Transform.Origin = new Point3(100, 100, 0);
        var e = Fails(() => Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "CirPattern1@plate-1" }));
        Assert.Equal("/components/0", e.Error.Details!["field"]);
    }

    [Fact]
    public void The_assembly_s_own_feature_cannot_drive()
    {
        Doc.AddFeature("LocalLPattern9", "LocalLPattern");
        var e = Fails(() => Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "LocalLPattern9" }));
        Assert.Equal("/driving_feature", e.Error.Details!["field"]);
    }

    [Fact]
    public void Two_instances_in_one_hole_roll_back()
    {
        Asm.PlaceInstance = (seed, i) => new Transform { Origin = OtherHoles[0] };
        var e = Fails(() => Driven(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "CirPattern1@plate-1" }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Empty(Asm.PatternItems);
        Assert.Equal(3, Asm.Items.Count);
    }
}
