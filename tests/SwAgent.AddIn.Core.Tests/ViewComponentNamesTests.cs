using SwAgent.AddIn.Core;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ViewComponentNamesTests
{
    [Fact]
    public void SP4_roots_a_drawing_views_components_at_the_view_s_own_root_component()
    {
        // Measured on seiraiyu2 (5.D live run, case 32): IView.GetVisibleComponents named pinblock's components so.
        Assert.Equal("pinblock-2", ViewComponentNames.Root(new[] { "pinblock-2/block-1", "pinblock-2/pin-1" }));
        Assert.Equal("pin-1", ViewComponentNames.Unrooted("pinblock-2/pin-1", "pinblock-2"));
    }

    [Fact]
    public void A_subassembly_s_component_keeps_its_path_in_the_assembly()
    {
        string? root = ViewComponentNames.Root(new[] { "top-1/sub-1", "top-1/sub-1/pin-1" });
        Assert.Equal("sub-1/pin-1", ViewComponentNames.Unrooted("top-1/sub-1/pin-1", root));
    }

    [Fact]
    public void Names_that_are_already_the_assembly_s_have_no_root()
    {
        Assert.Null(ViewComponentNames.Root(new[] { "block-1", "pin-1" }));
        Assert.Null(ViewComponentNames.Root(new[] { "a-1/block-1", "b-1/pin-1" }));
        Assert.Null(ViewComponentNames.Root(new string[0]));
        Assert.Equal("sub-1/pin-1", ViewComponentNames.Unrooted("sub-1/pin-1", null));
    }

    [Fact]
    public void A_name_without_the_root_is_left_as_it_is()
    {
        Assert.Equal("pin-1", ViewComponentNames.Unrooted("pin-1", "pinblock-2"));
        Assert.Equal("pinblock-20/pin-1", ViewComponentNames.Unrooted("pinblock-20/pin-1", "pinblock-2"));
    }
}
