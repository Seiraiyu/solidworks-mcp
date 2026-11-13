using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ExplodeViewTests : ExplodeTestBase
{
    ExplodeListResult List() => Run(new ExplodeListOperation(Services), new ExplodeListRequest { Document = "stand.SLDASM" });

    ExplodeShowResult Show(string view, bool? exploded = null) =>
        Run(new ExplodeShowOperation(Services), new ExplodeShowRequest { Document = "stand.SLDASM", View = view, Exploded = exploded });

    ExplodeDeleteResult Delete(string view) => Run(new ExplodeDeleteOperation(Services), new ExplodeDeleteRequest { Document = "stand.SLDASM", View = view });

    [Fact]
    public void Views_are_listed_by_configuration_with_steps_for_the_active_one()
    {
        Explode(Step("z", 40, "pin-1"), Step("x", 15, "bracket-1"));
        Doc.Configurations.Add("Other");
        Asm.Views.Add(new FakeExplodeView("ExplViewOther", "Other"));
        var r = List();
        Assert.Equal("Default", r.Configuration);
        Assert.Null(r.Exploded);
        var mine = r.Views.Single(v => v.Name == "ExplView1");
        Assert.Equal(new[] { "pin-1" }, mine.Steps![0].Components);
        Assert.Equal(15, mine.Steps[1].DistanceMm);
        Assert.Null(mine.Steps[1].AngleDeg);
        Assert.Null(r.Views.Single(v => v.Name == "ExplViewOther").Steps);
        Assert.Null(Asm.Shown);
    }

    [Fact]
    public void Show_explodes_and_collapses_and_says_what_was_shown_before()
    {
        Explode(Step("z", 40, "pin-1"));
        var shown = Show("explview1");
        Assert.True(shown.Exploded);
        Assert.Equal("ExplView1", shown.View);
        Assert.Null(shown.Before);
        Assert.Equal(40, Pin.Info.Transform.Origin.Z, 9);
        Assert.Equal("ExplView1", List().Exploded);
        var collapsed = Show("ExplView1", false);
        Assert.False(collapsed.Exploded);
        Assert.Equal("ExplView1", collapsed.Before);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
    }

    [Fact]
    public void Show_waits_while_the_user_edits_a_component_in_context()
    {
        Explode(Step("z", 40, "pin-1"));
        Asm.EditingTitle = "base.SLDPRT";
        var e = Fails(() => Show("ExplView1"));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Null(Asm.Shown);
    }

    [Fact]
    public void A_view_that_is_not_there_or_is_another_configurations_is_refused()
    {
        Doc.Configurations.Add("Other");
        Asm.Views.Add(new FakeExplodeView("ExplViewOther", "Other"));
        var none = Fails(() => Show("ExplView9"));
        Assert.Equal(ErrorCode.InvalidArgument, none.Code);
        Assert.Equal("/view", none.Error.Details!["field"]);
        var other = Fails(() => Delete("ExplViewOther"));
        Assert.Equal("Other", other.Error.Details!["configuration"]);
        Assert.Contains("configuration.activate", other.Message);
    }

    [Fact]
    public void Delete_collapses_a_shown_view_then_deletes_it()
    {
        Explode(Step("z", 40, "pin-1"));
        Show("ExplView1");
        var r = Delete("ExplView1");
        Assert.Equal("ExplView1", r.Deleted);
        Assert.Equal("Default", r.Configuration);
        Assert.Equal(1, r.Steps);
        Assert.Empty(Asm.Views);
        Assert.Null(Asm.Shown);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
        var changed = Assert.Single(r.Change.Changed);
        Assert.Equal("ExplView1", changed.Name);
        Assert.Equal("exploded", changed.Property);
    }

    [Fact]
    public void A_refused_delete_is_operation_failed_and_the_view_shown_again()
    {
        Explode(Step("z", 40, "pin-1"));
        Show("ExplView1");
        Asm.DeleteRefused = true;
        var e = Fails(() => Delete("ExplView1"));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Single(Asm.Views);
        Assert.Equal("ExplView1", Asm.Shown);
        Assert.Equal(40, Pin.Info.Transform.Origin.Z, 9);
        Assert.False(e.Error.Details!.ContainsKey("positions_restored"));
    }
}
