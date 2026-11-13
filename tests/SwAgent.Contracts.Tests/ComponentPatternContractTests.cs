using System;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class ComponentPatternContractTests
{
    [Fact]
    public void A_linear_pattern_request_names_its_directions_in_mm()
    {
        var request = new ComponentLinearPatternRequest
        {
            Components = { "pin-1" },
            Direction = "r1.face.abc",
            SpacingMm = 20,
            Count = 3,
            Direction2 = new ComponentPatternDirection { Direction = "Axis1@plate-1", SpacingMm = 15, Count = 2, Reverse = true },
        };
        string json = JsonCodec.Serialize(request);
        Assert.Contains("\"spacing_mm\":20", json);
        Assert.Contains("\"direction2\":{", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ComponentLinearPatternRequest>(json)));
    }

    [Fact]
    public void A_feature_pattern_names_its_driving_feature()
    {
        string json = JsonCodec.Serialize(new ComponentFeaturePatternRequest { Components = { "pin-1" }, DrivingFeature = "CirPattern1@plate-1" });
        Assert.Contains("\"driving_feature\":\"CirPattern1@plate-1\"", json);
    }

    [Fact]
    public void A_pattern_result_says_where_each_instance_landed()
    {
        var result = new ComponentPatternResult
        {
            Pattern = new ComponentPatternInfo { Name = "LocalLPattern1", Kind = ComponentPatternKind.Linear, Seeds = { "pin-1" }, Instances = { "pin-2" } },
            Sense = "along",
        };
        result.Instances.Add(new PatternInstance { Name = "pin-2", Seed = "pin-1", Origin = new Point3(20, 0, 0), Expected = new Point3(20, 0, 0), OffsetMm = 0, TurnDeg = 0 });
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"kind\":\"linear\"", json);
        Assert.Contains("\"offset_mm\":0", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ComponentPatternResult>(json)));
    }

    [Fact]
    public void An_interference_has_two_components_a_volume_and_a_box()
    {
        var result = new AssemblyInterferenceResult { Components = 3, TotalVolumeMm3 = 2000 };
        result.Interferences.Add(new InterferenceInfo
        {
            Components = { "block-1", "block-2" }, VolumeMm3 = 2000,
            Box = new Box3 { Min = new Point3(30, 0, 0), Max = new Point3(40, 20, 10) },
        });
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"volume_mm3\":2000", json);
        Assert.Contains("\"touching\":false", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<AssemblyInterferenceResult>(json)));
    }

    [Fact]
    public void Deleting_reports_patterns_and_their_instances()
    {
        var result = new ComponentDeleteResult { PatternsDeleted = { "LocalLPattern1" }, InstancesDeleted = { "pin-2", "pin-3" } };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"patterns_deleted\":[\"LocalLPattern1\"]", json);
        Assert.Contains("\"instances_deleted\":[\"pin-2\",\"pin-3\"]", json);
    }

    [Theory]
    [InlineData("component.linear_pattern", "component", false)]
    [InlineData("component.circular_pattern", "component", false)]
    [InlineData("component.feature_pattern", "component", false)]
    [InlineData("assembly.interference", "assembly", true)]
    public void The_operations_are_catalogued_in_their_families(string method, string family, bool readOnly)
    {
        var op = OperationCatalog.Find(method);
        Assert.NotNull(op);
        Assert.Equal(family, op!.Family);
        Assert.Equal(readOnly, op.ReadOnly);
        Assert.False(op.Internal);
    }

    [Fact]
    public void Diag_interference_is_internal()
    {
        Assert.True(OperationCatalog.Find("diag.interference")!.Internal);
    }

    [Fact]
    public void Patterns_have_a_minute_and_interference_two()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OperationFamilies.DefaultFor("component.feature_pattern", TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultFor("assembly.interference", TimeSpan.Zero));
    }

    [Fact]
    public void Protocol_11_and_later_speak_patterns()
    {
        Assert.True(Protocol.Version >= 11, $"protocol {Protocol.Version} speaks patterns from 11 on");
    }
}
