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
}
