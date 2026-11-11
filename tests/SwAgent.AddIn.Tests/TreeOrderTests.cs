using Xunit;

namespace SwAgent.AddIn.Tests;

// 4.A live run, round 5: IComponent2.GetChildren listed post-1 before base-1, although base-1 was inserted first and is
// first in the FeatureManager tree. assembly.components lists in tree order, so children are put in the tree's order.
public class TreeOrderTests
{
    [Fact]
    public void Children_follow_the_feature_tree()
    {
        var sorted = TreeOrder.Sort(new[] { "post-1", "base-1" }, new[] { "base-1", "post-1" }, n => n);
        Assert.Equal(new[] { "base-1", "post-1" }, sorted);
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case()
    {
        var sorted = TreeOrder.Sort(new[] { "sub-1/PIN-2", "sub-1/pin-1" }, new[] { "SUB-1/PIN-1", "sub-1/pin-2" }, n => n);
        Assert.Equal(new[] { "sub-1/pin-1", "sub-1/PIN-2" }, sorted);
    }

    [Fact]
    public void A_child_the_tree_does_not_name_keeps_its_place_after_those_it_does()
    {
        var sorted = TreeOrder.Sort(new[] { "c-1", "x-1", "a-1", "b-1" }, new[] { "b-1", "a-1" }, n => n);
        Assert.Equal(new[] { "b-1", "a-1", "c-1", "x-1" }, sorted);
    }

    [Fact]
    public void No_tree_leaves_the_order_as_it_was()
    {
        var sorted = TreeOrder.Sort(new[] { "post-1", "base-1" }, new string[0], n => n);
        Assert.Equal(new[] { "post-1", "base-1" }, sorted);
    }
}
