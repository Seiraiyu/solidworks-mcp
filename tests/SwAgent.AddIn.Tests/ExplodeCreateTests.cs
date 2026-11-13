using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ExplodeCreateTests : ExplodeTestBase
{
    [Fact]
    public void One_step_up_z_moves_the_pin_and_leaves_the_assembly_collapsed()
    {
        var r = Explode(Step("z", 40, "pin-1"));
        Assert.Equal("ExplView1", r.View.Name);
        Assert.Equal("Default", r.View.Configuration);
        Assert.False(r.View.Exploded);
        Assert.Single(r.View.Steps!);
        var step = Assert.Single(r.Steps);
        Assert.Equal("along", step.Sense);
        Assert.Null(step.TurnSense);
        var pin = Assert.Single(step.Components);
        Assert.Equal(40, pin.Actual.Z, 9);
        Assert.Equal(0, pin.OffsetMm, 9);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
        Assert.Null(Asm.Shown);
        var created = Assert.Single(r.Change.Created);
        Assert.Equal("ExplView1", created.Name);
        Assert.Equal(ReferenceKind.Feature, created.Kind);
        Assert.Empty(r.Change.Moved);
        Assert.Equal("step 1: fake: made Explode Step1", r.Tried);
    }

    [Fact]
    public void A_step_SOLIDWORKS_takes_the_other_way_says_against()
    {
        Asm.StepGoesAgainst = true;
        var r = Explode(Step("z", 40, "pin-1"));
        Assert.Equal("against", r.Steps[0].Sense);
        Assert.Equal(-40, r.Steps[0].Components[0].Actual.Z, 9);
    }

    [Fact]
    public void Each_step_starts_where_the_previous_ones_left_its_components()
    {
        var r = Explode(Step("z", 40, "pin-1"), Step(Ref(PlateEdge), 30, "pin-1", "bracket-1"));
        Assert.Equal(2, r.Steps.Count);
        var pin = r.Steps[1].Components.Single(c => c.Name == "pin-1");
        Assert.Equal(40, pin.Before.Z, 9);
        Assert.Equal(50, pin.Actual.X, 9);
        Assert.Equal(40, pin.Actual.Z, 9);
        var bracket = r.Steps[1].Components.Single(c => c.Name == "bracket-1");
        Assert.Equal(40, bracket.Actual.X, 9);
        Assert.Equal(10, Bracket.Info.Transform.Origin.X, 9);
    }

    [Fact]
    public void A_turning_step_moves_then_turns_about_its_axis()
    {
        var step = Step("z", 20, "bracket-1");
        step.RotateAbout = Ref(PlateHole);
        step.AngleDeg = 90;
        var r = Explode(step);
        var bracket = Assert.Single(r.Steps[0].Components);
        Assert.Equal("along", r.Steps[0].TurnSense);
        Assert.Equal(30, bracket.Actual.X, 9);
        Assert.Equal(10, bracket.Actual.Y, 9);
        Assert.Equal(30, bracket.Actual.Z, 9);
        Assert.Equal(0, bracket.TurnDeg, 6);
    }

    [Fact]
    public void A_step_that_lands_wrong_takes_the_view_back()
    {
        Asm.StepStrays = (c, i) => c.Info.Name == "pin-1" ? new Vector3(0, 0, 0.5) : null;
        var e = Fails(() => Explode(Step("z", 40, "pin-1")));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("explode_offset_mm", e.Error.Details!["check"]);
        Assert.Equal("/steps/0", e.Error.Details["field"]);
        Assert.Contains("ExplView1", (List<object?>)e.Error.Details["rolled_back"]!);
        Assert.Empty(Asm.Views);
        Assert.Null(Asm.Shown);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
    }

    [Fact]
    public void A_step_that_lands_one_of_its_components_wrong_names_that_one()
    {
        Asm.StepStrays = (c, i) => c.Info.Name == "pin-1" ? null : new Vector3(1, 0, 0);
        var e = Fails(() => Explode(Step("z", 40, "pin-1", "bracket-1")));
        Assert.Equal("explode_offset_mm", e.Error.Details!["check"]);
        Assert.Equal("bracket-1", e.Error.Details["component"]);
        Assert.Empty(Asm.Views);
    }

    [Fact]
    public void SOLIDWORKS_making_no_step_is_operation_failed_saying_what_was_tried()
    {
        Asm.StepFails = true;
        var e = Fails(() => Explode(Step("z", 40, "pin-1")));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("/steps/0", e.Error.Details!["field"]);
        Assert.Equal("fake: returned null", e.Error.Details["tried"]);
        Assert.Empty(Asm.Views);
    }

    [Fact]
    public void SOLIDWORKS_making_no_view_is_operation_failed()
    {
        Asm.ExplodeFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Explode(Step("z", 40, "pin-1"))).Code);
        Assert.Empty(Asm.Views);
    }

    [Fact]
    public void A_collapse_that_leaves_components_out_is_verification_failed_and_they_are_put_back()
    {
        Asm.CollapseStays = true;
        var e = Fails(() => Explode(Step("z", 40, "pin-1")));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("explode_collapsed", e.Error.Details!["check"]);
        Assert.Contains("pin-1", (List<object?>)e.Error.Details["positions_restored"]!);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
        Assert.Empty(Asm.Views);
    }

    [Fact]
    public void The_name_asked_is_given_and_a_refused_rename_takes_the_view_back()
    {
        var request = Request(Step("z", 40, "pin-1"));
        request.Name = "Pin out";
        var r = Explode(request);
        Assert.Equal("Pin out", r.View.Name);
        Assert.Equal("Pin out", Assert.Single(r.Change.Created).Name);

        Asm.RenameRefused = true;
        var again = Request(Step("z", 40, "pin-1"));
        again.Name = "Pin out again";
        var e = Fails(() => Explode(again));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("/name", e.Error.Details!["field"]);
        Assert.Equal(new[] { "Pin out" }, Asm.ExplodedViews("Default"));
    }

    [Fact]
    public void A_view_the_user_had_exploded_is_shown_again_afterwards()
    {
        string mine = Asm.CreateExplodedView()!;
        Asm.AddExplodeStep(new ExplodeStepSpec { Components = new[] { (object)Bracket.Entity }, Direction = PlateTop, DistanceMm = 10 }, out _);
        Asm.ShowExploded(mine, true);
        Assert.Equal(20, Bracket.Info.Transform.Origin.Z, 9);
        var r = Explode(Step("z", 40, "pin-1"));
        Assert.Equal("ExplView2", r.View.Name);
        Assert.Equal(mine, Asm.Shown);
        Assert.Equal(20, Bracket.Info.Transform.Origin.Z, 9);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
        Assert.Empty(r.Change.Moved);
    }

    [Fact]
    public void Requests_are_checked_before_SOLIDWORKS_is_asked()
    {
        void Refused(ExplodeCreateRequest request, string field)
        {
            var e = Fails(() => Explode(request));
            Assert.Equal(ErrorCode.InvalidArgument, e.Code);
            Assert.Equal(field, e.Error.Details!["field"]);
        }

        Doc.Configurations.Add("Other");
        Refused(Request(), "/steps");
        Refused(Request(Step("z", 40)), "/steps/0/components");
        Refused(Request(Step("z", 40, "nope-1")), "/steps/0/components/0");
        Refused(Request(Step("z", null, "pin-1")), "/steps/0/distance_mm");
        Refused(Request(Step("z", 0, "pin-1")), "/steps/0/distance_mm");
        Refused(Request(Step(Ref(PlateHole), 10, "pin-1")), "/steps/0/direction");
        var angleOnly = Step("z", 10, "pin-1");
        angleOnly.AngleDeg = 90;
        Refused(Request(angleOnly), "/steps/0/rotate_about");
        var axisOnly = Step("z", 10, "pin-1");
        axisOnly.RotateAbout = Ref(PlateHole);
        Refused(Request(axisOnly), "/steps/0/angle_deg");
        var flatAxis = Step("z", 10, "pin-1");
        flatAxis.RotateAbout = Ref(PlateTop);
        flatAxis.AngleDeg = 90;
        Refused(Request(flatAxis), "/steps/0/rotate_about");
        var other = Request(Step("z", 10, "pin-1"));
        other.Configuration = "Other";
        Refused(other, "/configuration");
        var blank = Request(Step("z", 10, "pin-1"));
        blank.Name = " ";
        Refused(blank, "/name");
        Assert.DoesNotContain("explode view", Asm.Calls);
    }

    [Fact]
    public void A_name_already_taken_in_the_configuration_is_refused()
    {
        var first = Request(Step("z", 10, "pin-1"));
        first.Name = "Pin out";
        Explode(first);
        var second = Request(Step("z", 20, "pin-1"));
        second.Name = "pin OUT";
        var e = Fails(() => Explode(second));
        Assert.Equal("/name", e.Error.Details!["field"]);
        Assert.Single(Asm.Views);
    }
}
