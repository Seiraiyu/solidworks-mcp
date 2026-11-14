using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class ViewCutContractTests
{
    [Fact]
    public void Protocol_14_speaks_sections_details_and_flat_patterns()
    {
        Assert.True(Protocol.Version >= 14, $"protocol {Protocol.Version} speaks sections, details and flat patterns from 14 on");
    }

    [Fact]
    public void A_model_place_is_a_point_in_model_mm_or_one_entity()
    {
        string point = JsonCodec.Serialize(new ModelPlace { Point = new Point3(20, 20, 10) });
        Assert.Equal("{\"point\":{\"x\":20,\"y\":20,\"z\":10}}", point);
        Assert.Equal(10, JsonCodec.Deserialize<ModelPlace>(point)!.Point!.Z);
        string named = JsonCodec.Serialize(new ModelPlace { Component = "pin-1", Entity = "Axis1" });
        Assert.Equal("{\"component\":\"pin-1\",\"entity\":\"Axis1\"}", named);
    }
}
