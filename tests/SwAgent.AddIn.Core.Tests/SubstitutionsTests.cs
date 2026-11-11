using System.Linq;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>Plan 4.B: component files SOLIDWORKS loaded from another folder than the assembly stores (4.A live run, case 7).</summary>
public class SubstitutionsTests
{
    [Fact]
    public void A_same_named_file_from_another_folder_is_a_substitute()
    {
        var found = Substitutions.Find(new[] { @"C:\run2\gone.SLDPRT", @"C:\run2\base.SLDPRT" }, new[] { @"C:\run1\gone.SLDPRT", @"C:\run2\base.SLDPRT" });
        var s = Assert.Single(found);
        Assert.Equal(@"C:\run2\gone.SLDPRT", s.Expected);
        Assert.Equal(@"C:\run1\gone.SLDPRT", s.Loaded);
    }

    [Fact]
    public void Paths_differing_only_in_case_are_the_same_file()
    {
        Assert.Empty(Substitutions.Find(new[] { @"C:\Run\Base.sldprt" }, new[] { @"c:\run\BASE.SLDPRT" }));
    }

    [Fact]
    public void A_file_found_nowhere_is_missing_not_substituted()
    {
        Assert.Empty(Substitutions.Find(new[] { @"C:\run2\gone.SLDPRT" }, new[] { @"C:\run2\gone.SLDPRT".Replace("gone", "base") }));
    }

    [Fact]
    public void A_file_stored_twice_is_reported_once()
    {
        var found = Substitutions.Find(new[] { @"C:\a\pin.SLDPRT", @"C:\a\pin.SLDPRT" }, new[] { @"C:\b\pin.SLDPRT" });
        Assert.Single(found);
        Assert.Equal(@"C:\b\pin.SLDPRT", found.Single().Loaded);
    }

    [Fact]
    public void Nothing_stored_is_nothing_substituted()
    {
        Assert.Empty(Substitutions.Find(new string[0], new[] { @"C:\b\pin.SLDPRT" }));
    }
}
