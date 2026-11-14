using System;
using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ExplodedImagesTests
{
    /// <summary>A front view at 1:2 with the model's origin drawn at (100, 150).</summary>
    static readonly ViewTransform Front = new ViewTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, 100, 150, 0, 0.5);

    static Transform At(double x, double y, double z) => new Transform { Origin = new Point3(x, y, z) };

    static Box3 Box(double x0, double y0, double z0, double x1, double y1, double z1) => new Box3 { Min = new Point3(x0, y0, z0), Max = new Point3(x1, y1, z1) };

    static SheetBox Sheet(double x0, double y0, double x1, double y1) => new SheetBox { Min = new SheetPoint(x0, y0), Max = new SheetPoint(x1, y1) };

    [Fact]
    public void A_move_is_drawn_turned_and_scaled()
    {
        var moved = ExplodedImages.Moved(Front, new Point3(20, 20, 0), new Point3(20, 60, 30));
        Assert.Equal(0, moved.X, 9);
        Assert.Equal(20, moved.Y, 9);
    }

    /// <summary>The pin (Ø10 x 30 along z at (20, 20)) moved 40 mm up along y; the block stays.</summary>
    static ComponentPlaces Pin(SheetBox? drawn) =>
        new ComponentPlaces("pin-1", At(20, 20, 0), At(20, 60, 0), Box(15, 55, 0, 25, 65, 30), Box(15, 15, 0, 25, 25, 30), drawn);

    static ComponentPlaces Block(SheetBox? drawn) =>
        new ComponentPlaces("block-1", At(0, 0, 0), At(0, 0, 0), Box(0, 0, 0, 60, 40, 20), Box(0, 0, 0, 60, 40, 20), drawn);

    [Fact]
    public void Components_drawn_inside_their_exploded_boxes_pass()
    {
        var (images, failure) = ExplodedImages.Check(Front, new[] { Block(Sheet(100, 150, 130, 170)), Pin(Sheet(107.5, 177.5, 112.5, 182.5)) });
        Assert.Null(failure);
        Assert.Equal(new[] { "block-1", "pin-1" }, images.ConvertAll(i => i.Name));
        Assert.Equal(20, images[1].Moved.Y, 9);
        Assert.Equal(177.5, images[1].Expected.Min.Y, 9);
    }

    [Fact]
    public void A_component_partly_hidden_still_passes_inside_its_box()
    {
        Assert.Null(ExplodedImages.Check(Front, new[] { Block(Sheet(100, 150, 130, 170)), Pin(Sheet(107.5, 180, 112.5, 182.5)) }).Failure);
    }

    [Fact]
    public void A_component_drawn_outside_its_exploded_box_fails_naming_it()
    {
        var failure = ExplodedImages.Check(Front, new[] { Block(Sheet(100, 150, 130, 170)), Pin(Sheet(107.5, 157.5, 112.5, 182.5)) }).Failure;
        Assert.NotNull(failure);
        Assert.Equal(("exploded_image", "pin-1"), (failure!.Value.Check, failure.Value.Component));
    }

    [Fact]
    public void A_view_left_collapsed_fails_as_not_moved()
    {
        // The pin's collapsed and exploded boxes overlap in this made-up case, so its collapsed image is inside both.
        var pin = new ComponentPlaces("pin-1", At(20, 20, 0), At(20, 24, 0), Box(15, 15, 0, 25, 29, 30), Box(15, 15, 0, 25, 25, 30), Sheet(107.5, 157.5, 112.5, 162.5));
        var failure = ExplodedImages.Check(Front, new[] { pin }).Failure;
        Assert.Equal("exploded_moved", failure!.Value.Check);
    }

    [Fact]
    public void A_move_toward_the_reader_is_not_drawn_and_not_held_to_moving()
    {
        var pin = new ComponentPlaces("pin-1", At(20, 20, 0), At(20, 20, 40), Box(15, 15, 40, 25, 25, 70), Box(15, 15, 0, 25, 25, 30), Sheet(107.5, 157.5, 112.5, 162.5));
        Assert.Null(ExplodedImages.Check(Front, new[] { pin }).Failure);
    }

    [Fact]
    public void A_view_that_draws_no_component_fails()
    {
        var failure = ExplodedImages.Check(Front, new[] { Block(null), Pin(null) }).Failure;
        Assert.Equal("exploded_image", failure!.Value.Check);
        Assert.Equal("none", failure.Value.Measured);
    }

    [Fact]
    public void A_component_without_a_box_is_left_out()
    {
        var suppressed = new ComponentPlaces("pin-2", At(0, 0, 0), At(0, 0, 0), null, null, null);
        var (images, failure) = ExplodedImages.Check(Front, new[] { Block(Sheet(100, 150, 130, 170)), suppressed });
        Assert.Null(failure);
        Assert.Single(images);
    }

    /// <summary>GetPolylines numbers for one straight edge from (x0, y0) to (x1, y1), metres about the view's position.</summary>
    static IEnumerable<double> Edge(double x0, double y0, double x1, double y1) => new double[] { 1, 0, 0, 1, 2, 3, 4, 5, 2, x0, y0, 0, x1, y1, 0 };

    [Fact]
    public void Each_components_drawn_box_is_the_union_of_its_edges()
    {
        var numbers = new List<double>();
        numbers.AddRange(Edge(0, 0, 0.06, 0));
        numbers.AddRange(Edge(0, 0, 0, 0.04));
        numbers.AddRange(Edge(0.015, 0.055, 0.025, 0.055));
        var boxes = ExplodedImages.PerComponent(new[] { "block-1", "block-1", "pin-1" }, numbers, new SheetPoint(100, 150), 0.5);
        Assert.Equal(130, boxes["block-1"].Max.X, 9);
        Assert.Equal(170, boxes["block-1"].Max.Y, 9);
        Assert.Equal(107.5, boxes["pin-1"].Min.X, 9);
        Assert.Equal(177.5, boxes["pin-1"].Min.Y, 9);
    }

    [Fact]
    public void Edges_of_no_component_are_left_out_and_a_count_that_disagrees_is_refused()
    {
        var numbers = new List<double>(Edge(0, 0, 0.01, 0));
        Assert.Empty(ExplodedImages.PerComponent(new string?[] { null }, numbers, new SheetPoint(0, 0), 1));
        Assert.Throws<FormatException>(() => ExplodedImages.PerComponent(new[] { "a", "b" }, numbers, new SheetPoint(0, 0), 1));
    }

    [Fact]
    public void An_edge_given_without_one_of_the_views_components_is_matched_to_the_component_that_shows_it()
    {
        // 5.E live run: with the drawing the active document, SP4 gave GetPolylines7's edges of an exploded view without
        // the component the view names (measured none for every component); each is found among a component's shown edges.
        var given = new string?[] { null, "make-pinblock-2/make-pin-1", "make-block-1", null };
        var shows = new Dictionary<string, Func<int, bool>>
        {
            ["make-block-1"] = i => i == 0 || i == 2,
            ["make-pin-1"] = i => i == 1,
        };
        var asked = new List<int>();
        var owners = ExplodedImages.Owners(given, new[] { "make-block-1", "make-pin-1" }, (name, i) =>
        {
            asked.Add(i);
            return shows[name](i);
        });
        Assert.Equal(new string?[] { "make-block-1", "make-pin-1", "make-block-1", null }, owners);
        Assert.DoesNotContain(2, asked);
    }

    [Fact]
    public void When_every_edge_names_one_of_the_views_components_nothing_is_matched()
    {
        var owners = ExplodedImages.Owners(new string?[] { "block-1", "PIN-1" }, new[] { "block-1", "pin-1" }, (_, _) => throw new InvalidOperationException("not asked"));
        Assert.Equal(new string?[] { "block-1", "PIN-1" }, owners);
    }
}
