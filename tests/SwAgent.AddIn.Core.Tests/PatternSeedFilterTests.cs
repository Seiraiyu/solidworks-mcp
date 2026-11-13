using SwAgent.AddIn.Core;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PatternSeedFilterTests
{
    const string Cube = @"C:\s\lcube.SLDPRT", Plate = @"C:\s\pplate.SLDPRT", Pin = @"C:\s\spin.SLDPRT";

    [Fact]
    public void A_parent_no_instance_copies_is_not_a_seed()
    {
        var seeds = PatternSeedFilter.FromParents(new[] { ("lcube-1", (string?)Cube), ("pplate-1", Plate) }, new[] { ("lcube-2", (string?)Cube) }, new[] { "pplate-1" });
        Assert.Equal(new[] { "lcube-1" }, seeds);
    }

    [Fact]
    public void A_copy_of_the_seeds_part_that_gives_the_direction_is_not_a_seed()
    {
        var seeds = PatternSeedFilter.FromParents(new[] { ("spin-1", (string?)Pin), ("spin-2", Pin) }, new[] { ("spin-3", (string?)Pin) }, new[] { "spin-2" });
        Assert.Equal(new[] { "spin-1" }, seeds);
    }

    [Fact]
    public void A_seed_patterned_along_its_own_edge_stays_the_seed()
    {
        var seeds = PatternSeedFilter.FromParents(new[] { ("spin-1", (string?)Pin) }, new[] { ("spin-2", (string?)Pin) }, new[] { "spin-1" });
        Assert.Equal(new[] { "spin-1" }, seeds);
    }

    [Fact]
    public void Without_a_direction_owner_every_copied_parent_is_a_seed_as_in_4D()
    {
        var seeds = PatternSeedFilter.FromParents(new[] { ("spin-1", (string?)Pin), ("spin-2", Pin) }, new[] { ("spin-3", (string?)Pin) }, new string[0]);
        Assert.Equal(new[] { "spin-1", "spin-2" }, seeds);
    }

    [Fact]
    public void Two_seeds_of_two_files_both_stay_and_the_order_is_the_parents()
    {
        var seeds = PatternSeedFilter.FromParents(
            new[] { ("spin-1", (string?)Pin), ("pplate-1", Plate), ("lcube-1", Cube) },
            new[] { ("lcube-2", (string?)Cube), ("spin-2", Pin) },
            new[] { "pplate-1" });
        Assert.Equal(new[] { "spin-1", "lcube-1" }, seeds);
    }
}
