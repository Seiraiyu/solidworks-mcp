using System.Collections.Generic;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class PatternLookupTests : ComponentPatternTestBase
{
    [Fact]
    public void Seeds_are_resolved_once_each()
    {
        var seeds = PatternSeeds.Resolve(Services.References, Doc, Asm, new List<string> { "pin-1", "pin-1", "block-1" });
        Assert.Equal(2, seeds.Count);
    }

    [Fact]
    public void No_seeds_is_invalid()
    {
        var e = Fails(() => PatternSeeds.Resolve(Services.References, Doc, Asm, new List<string>()));
        Assert.Equal("/components", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_component_inside_a_subassembly_is_not_a_seed()
    {
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
        Asm.AddComponent("sub-1/pin-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        var e = Fails(() => PatternSeeds.Resolve(Services.References, Doc, Asm, new List<string> { "pin-1", "sub-1/pin-1" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/components/1", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_suppressed_component_is_not_a_seed()
    {
        Pin.Info.State = ComponentState.Suppressed;
        var e = Fails(() => PatternSeeds.Resolve(Services.References, Doc, Asm, new List<string> { "pin-1" }));
        Assert.Equal("/components/0", e.Error.Details!["field"]);
        Assert.Contains("suppression", e.Message);
    }

    [Fact]
    public void A_pattern_instance_is_not_a_seed()
    {
        BlockRow();
        var e = Fails(() => PatternSeeds.Resolve(Services.References, Doc, Asm, new List<string> { "base-2" }));
        Assert.Contains("LocalLPattern1", e.Message);
    }

    [Fact]
    public void A_pattern_is_found_by_name_and_by_its_feature_reference()
    {
        var made = BlockRow();
        Assert.NotNull(PatternLookup.TryFind(Services.References, Doc, Asm, "LocalLPattern1", "/components/0"));
        Assert.NotNull(PatternLookup.TryFind(Services.References, Doc, Asm, made.Pattern.Reference!, "/components/0"));
        Assert.Null(PatternLookup.TryFind(Services.References, Doc, Asm, "block-1", "/components/0"));
    }

    [Fact]
    public void A_feature_that_is_not_a_pattern_is_refused()
    {
        var sketch = Doc.AddFeature("Sketch1", "ProfileFeature");
        var e = Fails(() => PatternLookup.TryFind(Services.References, Doc, Asm, Ref(sketch), "/components/0"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("not a component pattern", e.Message);
    }

    [Fact]
    public void Membership_knows_seeds_and_instances()
    {
        BlockRow();
        var m = PatternLookup.Membership(Asm);
        Assert.Equal("LocalLPattern1", m.InstanceOf["base-2"]);
        Assert.Equal(new[] { "LocalLPattern1" }, m.SeedOf["block-1"]);
    }

    [Fact]
    public void A_direction_resolves_and_is_described_in_the_assembly()
    {
        var side = PatternEntities.Resolve(Services.References, Doc, Ref(PlateEdge), null, "/direction", "/direction_component");
        Assert.Equal(GeometryType.Line, side.Info.Geometry);
        Assert.Equal("plate-1", side.Info.Component);
    }
}
