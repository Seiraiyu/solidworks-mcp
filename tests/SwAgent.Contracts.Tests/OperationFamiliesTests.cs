using System;
using System.Linq;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class OperationFamiliesTests
{
    [Fact]
    public void Every_family_in_the_catalog_has_a_default_deadline()
    {
        foreach (var family in OperationCatalog.All.Select(o => o.Family).Distinct())
            Assert.True(OperationFamilies.TryGetDefault(family, out _), $"family '{family}' has no default deadline");
    }

    [Fact]
    public void Defaults_are_the_designed_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), OperationFamilies.DefaultDeadline("session"));
        Assert.Equal(TimeSpan.FromSeconds(5), OperationFamilies.DefaultDeadline("addin"));
        Assert.Equal(TimeSpan.FromSeconds(10), OperationFamilies.DefaultDeadline("dialog"));
        Assert.Equal(TimeSpan.FromMinutes(10), OperationFamilies.DefaultDeadline("diag"));
        Assert.Equal(TimeSpan.FromSeconds(60), OperationFamilies.DefaultDeadline("document"));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultDeadline("sketch"));
        Assert.Equal(TimeSpan.FromSeconds(60), OperationFamilies.DefaultDeadline("feature"));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultDeadline("model"));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultDeadline("selection"));
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultDeadline("export"));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultDeadline("api"));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultDeadline("dimension"));
    }

    [Fact]
    public void A_method_gets_its_familys_default_and_an_unknown_one_the_fallback()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), OperationFamilies.DefaultFor("session.status", TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(30), OperationFamilies.DefaultFor("nope.nope", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void An_unknown_family_has_no_default()
    {
        Assert.False(OperationFamilies.TryGetDefault("nope", out _));
        Assert.Throws<ArgumentException>(() => OperationFamilies.DefaultDeadline("nope"));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(3_600_000L)]
    public void An_override_from_1_ms_to_1_hour_is_accepted(long ms)
    {
        OperationFamilies.ValidateOverride(ms);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(3_600_001L)]
    public void Any_other_override_is_invalid_argument(long ms)
    {
        var e = Assert.Throws<SwAgentException>(() => OperationFamilies.ValidateOverride(ms));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(ms, e.Error.Details!["deadline_ms"]);
    }
}
