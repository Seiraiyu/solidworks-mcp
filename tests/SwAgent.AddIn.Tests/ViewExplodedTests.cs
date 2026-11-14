using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ViewExplodedTests : AssemblyDrawingTestBase
{
    ViewResult Create(string? configuration = null, string? model = null) =>
        Run(new ViewCreateOperation(Services), new ViewCreateRequest
        {
            Model = model ?? Asm.Document.Path!, Orientation = "front", At = new SheetPoint(250, 150), Configuration = configuration, Exploded = true,
        });

    ViewResult Set(ViewSetRequest request)
    {
        request.View = AsmFront.Facts.Name;
        return Run(new ViewSetOperation(Services), request);
    }

    [Fact]
    public void An_assembly_view_made_exploded_draws_the_pin_where_its_step_puts_it()
    {
        var result = Create();
        Assert.True(result.View.Exploded);
        Assert.Equal("ExplView1", result.ExplodedView);
        var pin = result.Images!.Single(i => i.Name == "pin-1");
        Assert.Equal(0, pin.Moved.X, 9);
        Assert.Equal(40, pin.Moved.Y, 9);
        Assert.Equal(0, result.Images!.Single(i => i.Name == "block-1").Moved.Y, 9);
        Assert.Contains($"explode {result.View.Name}", Drw.Calls);
        // The assembly was shown exploded to read where the step puts the pin, and shows what it showed before.
        Assert.Null(Asm.Asm!.Shown);
        Assert.Equal(15, PinC.Info.Box!.Min.Y, 9);
    }

    [Fact]
    public void A_part_is_refused_before_SOLIDWORKS_is_asked()
    {
        var e = Fails(() => Create(model: Block.Document.Path));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/exploded", e.Error.Details!["field"]);
        Assert.DoesNotContain(Drw.Calls, c => c.StartsWith("model view"));
    }

    [Fact]
    public void A_configuration_without_an_exploded_view_points_at_explode_create_and_the_view_goes_again()
    {
        Asm.Asm!.Views.Single().Steps.Clear();
        var e = Fails(() => Create());
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/exploded", e.Error.Details!["field"]);
        Assert.Contains("sw_explode_create", e.Message);
        Assert.Single(Drw.Sheet("Sheet1").Views);
    }

    [Fact]
    public void A_configuration_the_assembly_does_not_have_active_is_refused_naming_it()
    {
        Drw.Configurations[Asm.Document.Path!] = new System.Collections.Generic.List<string> { "Default", "Long" };
        var e = Fails(() => Create("Long"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/configuration", e.Error.Details!["field"]);
        Assert.Contains("sw_configuration_activate", e.Message);
        Assert.Single(Drw.Sheet("Sheet1").Views);
    }

    [Fact]
    public void A_view_SOLIDWORKS_leaves_collapsed_is_refused_and_deleted()
    {
        Drw.ShowExplodedIgnored = true;
        var e = Fails(() => Create());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("exploded", e.Error.Details!["check"]);
        Assert.Single(Drw.Sheet("Sheet1").Views);
    }

    [Fact]
    public void A_view_that_says_exploded_but_draws_collapsed_fails_naming_the_pin()
    {
        // The pin moves 40 mm, more than its own size, so drawn collapsed it is outside its exploded box.
        Drw.ExplodedDrawsCollapsed = true;
        var e = Fails(() => Create());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("exploded_image", e.Error.Details!["check"]);
        Assert.Equal("pin-1", e.Error.Details["component"]);
    }

    [Fact]
    public void A_view_that_draws_a_component_out_of_place_fails_naming_it()
    {
        Drw.ImagesOffByMm = 5;
        var e = Fails(() => Create());
        Assert.Equal("exploded_image", e.Error.Details!["check"]);
    }

    [Fact]
    public void Exploding_must_not_mark_the_assembly_as_changed()
    {
        Drw.ExplodingDirtiesModel = true;
        var e = Fails(() => Create());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Single(Drw.Sheet("Sheet1").Views);
    }

    [Fact]
    public void View_set_explodes_a_view_and_collapses_it_again()
    {
        var exploded = Set(new ViewSetRequest { Exploded = true });
        Assert.True(exploded.View.Exploded);
        Assert.Equal("ExplView1", exploded.ExplodedView);
        var change = Assert.Single(exploded.Change.Changed);
        Assert.Equal(("exploded", "false", "true"), (change.Property, change.Before, change.After));
        var collapsed = Set(new ViewSetRequest { Exploded = false });
        Assert.False(collapsed.View.Exploded);
        Assert.Null(collapsed.Images);
    }

    [Fact]
    public void An_exploded_view_moved_draws_what_it_drew_moved()
    {
        Set(new ViewSetRequest { Exploded = true });
        var moved = Set(new ViewSetRequest { At = new SheetPoint(260, 150) });
        Assert.True(moved.View.Exploded);
        Assert.Equal(260, moved.View.Position.X, 9);
    }

    [Fact]
    public void A_failed_explode_puts_the_view_back_collapsed()
    {
        Drw.ExplodedDrawsCollapsed = true;
        var e = Fails(() => Set(new ViewSetRequest { Exploded = true }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.False(AsmFront.Facts.Exploded);
    }

    [Fact]
    public void Only_a_model_view_of_an_assembly_is_exploded()
    {
        var part = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var e = Fails(() => Run(new ViewSetOperation(Services), new ViewSetRequest { View = part.Facts.Name, Exploded = true }));
        Assert.Equal("/exploded", e.Error.Details!["field"]);
        Assert.Contains("a part", e.Message);
        string right = Run(new ViewProjectOperation(Services), new ViewProjectRequest { Parent = AsmFront.Facts.Name, Direction = ProjectDirection.Right }).View.Name;
        var projected = Fails(() => Run(new ViewSetOperation(Services), new ViewSetRequest { View = right, Exploded = true }));
        Assert.Equal("/exploded", projected.Error.Details!["field"]);
    }
}
