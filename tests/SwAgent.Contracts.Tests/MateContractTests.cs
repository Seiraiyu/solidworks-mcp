using System;
using System.Linq;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class MateContractTests
{
    [Theory]
    [InlineData(MateType.Coincident, "coincident")]
    [InlineData(MateType.Concentric, "concentric")]
    [InlineData(MateType.Lock, "lock")]
    public void Mate_types_have_snake_case_wire_names(MateType type, string wire) => Assert.Equal(wire, WireNames.ForEnum(type));

    [Fact]
    public void Alignment_anti_aligned_is_snake_case() => Assert.Equal("anti_aligned", WireNames.ForEnum(MateAlignment.AntiAligned));

    [Fact]
    public void A_mate_request_names_each_side_and_its_value_in_mm()
    {
        var request = new MateAddRequest
        {
            Type = MateType.Distance,
            Entities = { new MateEntity { Entity = "r1.face.abc" }, new MateEntity { Entity = "r1.face.def", Component = "pin-1" } },
            DistanceMm = 15,
            Alignment = MateAlignment.AntiAligned,
        };
        string json = JsonCodec.Serialize(request);
        Assert.Contains("\"distance_mm\":15", json);
        Assert.Contains("\"component\":\"pin-1\"", json);
        Assert.Contains("\"alignment\":\"anti_aligned\"", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<MateAddRequest>(json)));
    }

    [Fact]
    public void A_mate_result_carries_what_was_measured()
    {
        var result = new MateAddResult
        {
            Mate = new MateInfo { Name = "Concentric1", Type = MateType.Concentric, SolidWorksType = "swMateCONCENTRIC", Alignment = MateAlignment.Aligned },
            Measured = { new MateMeasurement { Condition = "axis_offset_mm", Expected = 0, Measured = 0.0002, Tolerance = 0.001 } },
        };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"axis_offset_mm\"", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<MateAddResult>(json)));
    }

    [Fact]
    public void Free_motion_round_trips_with_absent_counts_when_unknown()
    {
        var f = new ComponentFreedom { Name = "pin-1", Constraint = Constraint.UnderDefined, Note = "SOLIDWORKS failed" };
        string json = JsonCodec.Serialize(f);
        Assert.DoesNotContain("free_translations", json);
        var known = new ComponentFreedom { Name = "pin-1", Constraint = Constraint.UnderDefined, FreeTranslations = 1, FreeRotations = 1 };
        known.Translations.Add(new Vector3(0, 0, 1));
        known.Rotations.Add(new FreeRotation { Axis = new Vector3(0, 0, 1), Through = new Point3(20, 10, 0) });
        string back = JsonCodec.Serialize(JsonCodec.Deserialize<ComponentFreedom>(JsonCodec.Serialize(known)));
        Assert.Contains("\"free_rotations\":1", back);
    }

    [Theory]
    [InlineData("mate.add", "mate", false)]
    [InlineData("mate.edit", "mate", false)]
    [InlineData("mate.delete", "mate", false)]
    [InlineData("mate.list", "mate", true)]
    [InlineData("assembly.dof", "assembly", true)]
    public void The_operations_are_catalogued_in_their_families(string method, string family, bool readOnly)
    {
        var op = OperationCatalog.Find(method);
        Assert.NotNull(op);
        Assert.Equal(family, op!.Family);
        Assert.Equal(readOnly, op.ReadOnly);
    }

    [Fact]
    public void Mates_have_a_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), OperationFamilies.DefaultFor("mate.add", TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultFor("assembly.dof", TimeSpan.Zero));
    }

    [Fact]
    public void Protocol_9_speaks_mates()
    {
        Assert.True(Protocol.Version >= 9, $"protocol {Protocol.Version} speaks mates from 9 on");
        Assert.Equal(ReferenceKind.Mate, References.KindOf(References.Encode(new ReferenceData(ReferenceKind.Mate, "a.SLDASM", new byte[] { 1 }, "Coincident1"))));
    }
}
