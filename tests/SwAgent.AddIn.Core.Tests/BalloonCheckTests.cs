using System.Collections.Generic;
using SwAgent.AddIn.Core;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class BalloonCheckTests
{
    /// <summary>block-1, and two pins of one file and configuration.</summary>
    static readonly string[] Visible = { "block-1", "pin-1", "pin-2" };

    static string Item(string component) => component.StartsWith("pin") ? "PIN|DEFAULT" : "BLOCK|DEFAULT";

    static BalloonFacts B(string name, string? number, params string[] components) => new BalloonFacts(name, number, components);

    static readonly Dictionary<string, string> Bom = new Dictionary<string, string> { ["BLOCK|DEFAULT"] = "1", ["PIN|DEFAULT"] = "2" };

    [Fact]
    public void One_balloon_per_item_numbered_as_the_bill_passes()
    {
        Assert.Null(BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1"), B("b2", "2", "pin-2") }, new BalloonFacts[0], Bom));
    }

    [Fact]
    public void Without_a_bill_an_item_has_one_number_and_items_differ()
    {
        Assert.Null(BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1"), B("b2", "2", "pin-1") }, new BalloonFacts[0], null));
        var same = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1"), B("b2", "1", "pin-1") }, new BalloonFacts[0], null);
        Assert.Equal("balloon_item", same!.Value.Check);
    }

    [Fact]
    public void A_number_the_bill_gives_another_item_fails()
    {
        var wrong = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "2", "block-1"), B("b2", "1", "pin-1") }, new BalloonFacts[0], Bom);
        Assert.Equal(("balloon_item", "b1"), (wrong!.Value.Check, wrong.Value.Balloon));
        Assert.Contains("item 1", wrong.Value.Expected);
    }

    [Fact]
    public void An_item_left_without_a_balloon_fails()
    {
        var left = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1") }, new BalloonFacts[0], Bom);
        Assert.Equal("balloon_count", left!.Value.Check);
        Assert.Contains("pin-1 or pin-2", left.Value.Expected);
    }

    [Fact]
    public void An_item_already_ballooned_needs_no_new_one()
    {
        Assert.Null(BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1") }, new[] { B("old", "2", "pin-2") }, Bom));
    }

    [Fact]
    public void Two_new_balloons_on_one_item_fail()
    {
        var twice = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1"), B("b2", "2", "pin-1"), B("b3", "2", "pin-2") }, new BalloonFacts[0], Bom);
        Assert.Equal("balloon_count", twice!.Value.Check);
    }

    [Theory]
    [InlineData()]
    [InlineData("washer-1")]
    [InlineData("block-1", "pin-1")]
    public void A_balloon_must_be_on_exactly_one_component_the_view_shows(params string[] components)
    {
        var bad = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", components) }, new BalloonFacts[0], Bom);
        Assert.Equal(("balloon_component", "b1"), (bad!.Value.Check, bad.Value.Balloon));
    }

    [Fact]
    public void A_balloon_without_a_number_fails()
    {
        Assert.Equal("balloon_item", BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", " ", "block-1") }, new BalloonFacts[0], Bom)!.Value.Check);
    }

    [Fact]
    public void A_balloon_attached_to_no_component_says_what_it_is_attached_to()
    {
        var unattached = new BalloonFacts("b2", "2", new string[0]) { AttachedNote = "IEdge (no component)" };
        var m = BalloonCheck.Mismatch(Visible, Item, new[] { B("b1", "1", "block-1"), unattached }, new BalloonFacts[0], null);
        Assert.Equal("balloon_component", m!.Value.Check);
        Assert.Equal("attached to none (IEdge (no component))", m.Value.Measured);
    }
}
