using System;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class FlatPatternContractTests
{
    [Fact]
    public void Protocol_10_speaks_flat_pattern_export()
    {
        Assert.Equal(10, Protocol.Version);
    }

    [Fact]
    public void The_flat_pattern_export_is_an_export_and_an_MCP_tool()
    {
        var op = OperationCatalog.Find("export.flat_pattern")!;
        Assert.Equal("export", op.Family);
        Assert.Equal(typeof(ExportFlatPatternRequest), op.RequestType);
        Assert.Equal(typeof(ExportFlatPatternResult), op.ResultType);
        Assert.False(op.ReadOnly);
        Assert.False(op.Internal);
        Assert.Equal("sw_export_flat_pattern", op.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultFor("export.flat_pattern", TimeSpan.Zero));
    }

    [Fact]
    public void It_needs_a_path_and_a_format_and_nothing_else()
    {
        Assert.Equal(new[] { "path", "format" }, SheetMetalBendContractTests.Required(typeof(ExportFlatPatternRequest)).ToArray());
    }

    [Fact]
    public void Its_formats_are_dxf_and_dwg_on_the_wire()
    {
        string json = JsonCodec.Serialize(new ExportFlatPatternRequest { Path = @"C:\cut\box.dwg", Format = FlatPatternFormat.Dwg, BendLines = false });
        Assert.Contains("\"format\":\"dwg\"", json);
        Assert.Contains("\"bend_lines\":false", json);
        var back = JsonCodec.Deserialize<ExportFlatPatternRequest>("{\"path\":\"C:\\\\cut\\\\box.dxf\",\"format\":\"dxf\"}");
        Assert.Equal(FlatPatternFormat.Dxf, back.Format);
        Assert.Null(back.BendLines);
        Assert.Null(back.Sketches);
    }

    [Fact]
    public void Its_result_carries_the_extents_and_the_flat_size_it_was_checked_against()
    {
        var result = new ExportFlatPatternResult
        {
            Format = FlatPatternFormat.Dxf,
            Header = "DXF",
            Method = "ExportFlatPatternView",
            State = SheetState.Folded,
            Flat = new FlatSize { LengthMm = 136.57, WidthMm = 96.57, Bends = 4 },
            Extents = new DrawingExtents { MinXMm = 0, MinYMm = 0, MaxXMm = 136.5664, MaxYMm = 96.5664, LengthMm = 136.5664, WidthMm = 96.5664, From = "$EXTMIN/$EXTMAX" },
            BendLines = 4,
        };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"method\":\"ExportFlatPatternView\"", json);
        Assert.Contains("\"extents\":{", json);
        Assert.Contains("\"from\":\"$EXTMIN/$EXTMAX\"", json);
        Assert.Contains("\"bend_lines\":4", json);
        Assert.Contains("\"state\":\"folded\"", json);
    }
}
