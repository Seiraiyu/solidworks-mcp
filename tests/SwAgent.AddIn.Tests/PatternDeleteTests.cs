using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class PatternDeleteTests : ComponentPatternTestBase
{
    ComponentDeleteResult Delete(params string[] items) =>
        Run(new ComponentDeleteOperation(Services), new ComponentDeleteRequest { Document = "stand.SLDASM", Components = new List<string>(items) });

    [Fact]
    public void A_pattern_goes_with_its_instances_and_its_seed_stays()
    {
        BlockRow();
        var r = Delete("LocalLPattern1");
        Assert.Equal(new[] { "LocalLPattern1" }, r.PatternsDeleted);
        Assert.Equal(2, r.InstancesDeleted.Count);
        Assert.Empty(r.Deleted);
        Assert.NotNull(Asm.ComponentByName("block-1"));
        Assert.Empty(Asm.PatternItems);
    }

    [Fact]
    public void A_pattern_by_reference_and_its_seed_go_together()
    {
        var made = BlockRow();
        var r = Delete(made.Pattern.Reference!, "block-1");
        Assert.Equal(new[] { "block-1" }, r.Deleted);
        Assert.Single(r.PatternsDeleted);
        Assert.Null(Asm.ComponentByName("block-1"));
    }

    [Fact]
    public void A_seed_alone_is_refused_naming_its_pattern()
    {
        BlockRow();
        var e = Fails(() => Delete("block-1"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/components/0", e.Error.Details!["field"]);
        Assert.Equal(new object?[] { "LocalLPattern1" }, (List<object?>)e.Error.Details["patterns"]!);
        Assert.DoesNotContain(Asm.Calls, c => c.StartsWith("delete"));
    }

    [Fact]
    public void One_instance_is_refused_naming_its_pattern()
    {
        BlockRow();
        var e = Fails(() => Delete("base-2"));
        Assert.Equal("LocalLPattern1", e.Error.Details!["pattern"]);
    }

    [Fact]
    public void Plain_components_are_deleted_as_before()
    {
        var r = Delete("pin-1");
        Assert.Equal(new[] { "pin-1" }, r.Deleted);
        Assert.Empty(r.PatternsDeleted);
    }
}
