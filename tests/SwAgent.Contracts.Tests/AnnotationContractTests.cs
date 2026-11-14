using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class AnnotationContractTests
{
    [Fact]
    public void Protocol_15_speaks_dimensions_and_annotations()
    {
        Assert.Equal(15, Protocol.Version);
    }

    [Theory]
    [InlineData(AnnotationKind.CentreMark, "centre_mark")]
    [InlineData(AnnotationKind.CentreLine, "centre_line")]
    [InlineData(AnnotationKind.Gtol, "gtol")]
    public void Annotation_kinds_have_snake_case_wire_names(AnnotationKind kind, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(kind));
    }

    [Theory]
    [InlineData(GtolCharacteristic.ProfileOfALine, "profile_of_a_line")]
    [InlineData(GtolCharacteristic.ProfileOfASurface, "profile_of_a_surface")]
    [InlineData(GtolCharacteristic.CircularRunout, "circular_runout")]
    [InlineData(GtolCharacteristic.Position, "position")]
    public void The_fourteen_characteristics_have_their_names(GtolCharacteristic characteristic, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(characteristic));
        Assert.Equal(14, WireNames.EnumNames(typeof(GtolCharacteristic)).Count);
    }

    [Fact]
    public void An_annotation_carries_its_dimension_frame_and_attachments()
    {
        var info = new AnnotationInfo
        {
            Name = "RD1@Drawing View1", Kind = AnnotationKind.Dimension, Sheet = "Sheet1", View = "Drawing View1", Position = new SheetPoint(120, 230),
            DimensionKind = DimensionKind.Horizontal, Value = 60, DimensionName = "RD1@Drawing View1", ModelDimension = false,
            Attached = new List<ViewEntity> { new ViewEntity { View = "Drawing View1", Reference = "r1.edge.x" } },
        };
        string json = JsonCodec.Serialize(info);
        Assert.Contains("\"kind\":\"dimension\"", json);
        Assert.Contains("\"dimension_kind\":\"horizontal\"", json);
        Assert.Contains("\"model_dimension\":false", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<AnnotationInfo>(json)));
        var frame = JsonCodec.Serialize(new GtolFrame { Characteristic = GtolCharacteristic.Position, ToleranceMm = 0.1, Diameter = true, MaterialCondition = MaterialCondition.Mmc, Datums = { "A" } });
        Assert.Equal("{\"characteristic\":\"position\",\"tolerance_mm\":0.1,\"diameter\":true,\"material_condition\":\"mmc\",\"datums\":[\"A\"]}", frame);
    }
}
