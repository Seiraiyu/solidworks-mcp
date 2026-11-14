using System.Collections.Generic;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// A drawing always has an active sketch (5.A live run: GetActiveSketch2 gives the current sheet's own sketch, or the
/// activated view's): that is the drawing at rest, not the user editing. Any other active sketch is a user edit and busy.
/// Sketching on the sheet runs a command and editing the sheet format clears GetEditSheet; both are busy elsewhere.
/// </summary>
public class DrawingSketchTests
{
    static readonly object SheetSketch = new object(), ViewSketch = new object(), Other = new object();

    static List<DrawingSketches.Sketch> AtRest() => new List<DrawingSketches.Sketch>
    {
        new DrawingSketches.Sketch(SheetSketch, "Sketch1"),
        new DrawingSketches.Sketch(ViewSketch, "Sketch3"),
    };

    [Fact]
    public void No_active_sketch_is_no_edit()
    {
        Assert.Null(DrawingSketches.UserEditing(null, AtRest()));
    }

    [Fact]
    public void The_sheets_own_sketch_is_the_drawing_at_rest()
    {
        Assert.Null(DrawingSketches.UserEditing(new DrawingSketches.Sketch(SheetSketch, "Sketch1"), AtRest()));
    }

    [Fact]
    public void The_activated_views_own_sketch_is_the_drawing_at_rest()
    {
        Assert.Null(DrawingSketches.UserEditing(new DrawingSketches.Sketch(ViewSketch, "Sketch3"), AtRest()));
    }

    [Fact]
    public void The_same_sketch_through_another_wrapper_is_matched_by_name()
    {
        Assert.Null(DrawingSketches.UserEditing(new DrawingSketches.Sketch(new object(), "Sketch1"), AtRest()));
    }

    [Fact]
    public void Any_other_active_sketch_is_the_user_editing_it()
    {
        Assert.Equal("Sketch7", DrawingSketches.UserEditing(new DrawingSketches.Sketch(Other, "Sketch7"), AtRest()));
    }

    [Fact]
    public void An_unnamed_other_sketch_is_still_an_edit()
    {
        Assert.Equal("a sketch", DrawingSketches.UserEditing(new DrawingSketches.Sketch(Other, null), AtRest()));
    }

    [Fact]
    public void With_nothing_known_at_rest_an_active_sketch_is_an_edit()
    {
        Assert.Equal("Sketch1", DrawingSketches.UserEditing(new DrawingSketches.Sketch(SheetSketch, "Sketch1"), new List<DrawingSketches.Sketch>()));
    }

    // The 5.C live run (round 2): the user's Edit Sketch on section line A (Drawing View1's own sketch) made that sketch
    // active exactly as the add-in's ActivateView does, so the activated view's sketch is at rest only when the add-in
    // activated that view.
    static readonly DrawingSketches.Sketch Sheet = new DrawingSketches.Sketch(SheetSketch, "Sketch1"), View = new DrawingSketches.Sketch(ViewSketch, "Sketch3");

    [Fact]
    public void The_activated_views_sketch_is_at_rest_when_the_add_in_activated_the_view()
    {
        Assert.Null(DrawingSketches.UserEditing(View, Sheet, "Drawing View1", View, "Drawing View1"));
        Assert.Null(DrawingSketches.UserEditing(Sheet, Sheet, "Drawing View1", View, null));
    }

    [Fact]
    public void The_activated_views_sketch_is_the_user_editing_it_when_the_add_in_did_not_activate_the_view()
    {
        Assert.Equal("Sketch3", DrawingSketches.UserEditing(View, Sheet, "Drawing View1", View, null));
        Assert.Equal("Sketch3", DrawingSketches.UserEditing(View, Sheet, "Drawing View1", View, "Drawing View2"));
    }

    [Fact]
    public void A_view_active_when_an_operation_ends_is_the_add_ins_and_is_let_go_of()
    {
        // The 5.C live run (round 6): SOLIDWORKS activated Drawing View1 itself when a dimension was inserted in it, and
        // the next change was refused as the user editing its sketch.
        ViewActivations.Clear("d");
        var activated = new List<string>();
        Assert.Null(ViewActivations.Settle("d", "Drawing View1", "Sheet1", adopt: true, activate: v => { activated.Add(v); return true; }, atRest: () => true));
        Assert.Equal(new[] { "Sheet1" }, activated);
        Assert.Null(ViewActivations.ByAddIn("d"));
        // Not at rest after letting go: still the add-in's, and said to be.
        Assert.Equal("Drawing View1", ViewActivations.Settle("d", "Drawing View1", "Sheet1", adopt: true, activate: v => true, atRest: () => false));
        Assert.Equal("Drawing View1", ViewActivations.ByAddIn("d"));
        ViewActivations.Clear("d");
    }

    [Fact]
    public void A_view_the_user_activated_is_never_adopted_outside_an_operation()
    {
        ViewActivations.Clear("d");
        var activated = new List<string>();
        ViewActivations.Settle("d", "Drawing View1", "Sheet1", adopt: false, activate: v => { activated.Add(v); return true; }, atRest: () => true);
        Assert.Empty(activated);
        Assert.Null(ViewActivations.ByAddIn("d"));
        ViewActivations.Record("d", "Drawing View1");
        ViewActivations.Settle("d", "Drawing View1", "Sheet1", adopt: false, activate: v => { activated.Add(v); return true; }, atRest: () => true);
        Assert.Equal(new[] { "Sheet1" }, activated);
        Assert.Null(ViewActivations.ByAddIn("d"));
    }

    [Fact]
    public void Nothing_is_activated_when_no_view_is_or_the_sheet_is()
    {
        ViewActivations.Clear("d");
        var activated = new List<string>();
        ViewActivations.Settle("d", null, "Sheet1", adopt: true, activate: v => { activated.Add(v); return true; }, atRest: () => true);
        ViewActivations.Settle("d", "Sheet1", "Sheet1", adopt: true, activate: v => { activated.Add(v); return true; }, atRest: () => true);
        Assert.Empty(activated);
        Assert.Null(ViewActivations.ByAddIn("d"));
    }

    [Fact]
    public void The_add_in_remembers_the_view_it_activated_until_the_drawing_is_settled()
    {
        ViewActivations.Clear("block - Sheet1");
        Assert.Null(ViewActivations.ByAddIn("block - Sheet1"));
        ViewActivations.Record("block - Sheet1", "Drawing View1");
        Assert.Equal("Drawing View1", ViewActivations.ByAddIn("block - Sheet1"));
        Assert.Null(ViewActivations.ByAddIn("formats - Sheet1"));
        ViewActivations.Clear("block - Sheet1");
        Assert.Null(ViewActivations.ByAddIn("block - Sheet1"));
    }
}
