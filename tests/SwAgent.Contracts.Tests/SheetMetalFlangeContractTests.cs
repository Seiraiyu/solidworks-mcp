using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class SheetMetalFlangeContractTests
{
    [Theory]
    [InlineData(BendPosition.MaterialInside, "material_inside")]
    [InlineData(BendPosition.MaterialOutside, "material_outside")]
    [InlineData(BendPosition.BendOutside, "bend_outside")]
    public void Bend_positions_have_snake_case_wire_names(BendPosition position, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(position));
    }

    [Theory]
    [InlineData(FlangeLengthFrom.OuterVirtualSharp, "outer_virtual_sharp")]
    [InlineData(FlangeLengthFrom.InnerVirtualSharp, "inner_virtual_sharp")]
    [InlineData(FlangeLengthFrom.Tangent, "tangent")]
    public void Flange_lengths_are_measured_from_named_places(FlangeLengthFrom from, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(from));
    }

    [Theory]
    [InlineData(HemKind.Closed, "closed")]
    [InlineData(HemKind.Open, "open")]
    [InlineData(HemKind.Teardrop, "teardrop")]
    [InlineData(HemKind.Rolled, "rolled")]
    public void Hem_kinds_have_their_wire_names(HemKind kind, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(kind));
    }

    [Fact]
    public void A_feature_result_lists_its_features_only_when_it_made_more_than_one()
    {
        Assert.DoesNotContain("\"features\"", JsonCodec.Serialize(new SheetMetalFeatureResult()));
        var two = new SheetMetalFeatureResult
        {
            Feature = new EntityInfo { Kind = ReferenceKind.Feature, Name = "Edge-Flange1" },
            Features = new List<EntityInfo> { new EntityInfo { Name = "Edge-Flange1" }, new EntityInfo { Name = "Edge-Flange2" } },
        };
        var back = JsonCodec.Deserialize<SheetMetalFeatureResult>(JsonCodec.Serialize(two));
        Assert.Equal(new[] { "Edge-Flange1", "Edge-Flange2" }, back.Features!.ConvertAll(f => f.Name));
    }
}
