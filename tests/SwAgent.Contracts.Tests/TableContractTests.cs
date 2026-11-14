using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class TableContractTests
{
    [Fact]
    public void Protocol_16_speaks_assembly_drawings()
    {
        Assert.Equal(16, Protocol.Version);
    }

    [Theory]
    [InlineData(BomKind.TopLevel, "top_level")]
    [InlineData(BomKind.PartsOnly, "parts_only")]
    [InlineData(BomKind.Indented, "indented")]
    public void Bom_kinds_have_snake_case_wire_names(BomKind kind, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(kind));
    }

    [Theory]
    [InlineData(TableAnchor.TopLeft, "top_left")]
    [InlineData(TableAnchor.BottomRight, "bottom_right")]
    public void Table_anchors_have_snake_case_wire_names(TableAnchor anchor, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(anchor));
    }

    [Fact]
    public void Balloons_have_six_layouts_and_are_a_kind_of_annotation()
    {
        Assert.Equal(new[] { "square", "circular", "top", "bottom", "left", "right" }, WireNames.EnumNames(typeof(BalloonLayout)));
        Assert.Equal("balloon", WireNames.ForEnum(AnnotationKind.Balloon));
    }

    [Fact]
    public void A_bom_reads_back_its_rows_with_their_files_and_quantities()
    {
        var table = new TableInfo
        {
            Name = "Bill of Materials1", Sheet = "Assembly", View = "Drawing View9", Kind = TableKind.Bom, BomKind = BomKind.TopLevel, Configuration = "Default",
            Columns = { "ITEM NO.", "PART NUMBER", "DESCRIPTION", "QTY." },
            Rows =
            {
                new TableRow { Cells = { "1", "block", "", "1" }, ItemNumber = "1", Quantity = 1, Path = @"C:\scratch\block.SLDPRT", Configuration = "Default", Components = { "block-1" } },
            },
        };
        string json = JsonCodec.Serialize(table);
        Assert.Contains("\"kind\":\"bom\"", json);
        Assert.Contains("\"bom_kind\":\"top_level\"", json);
        Assert.Contains("\"item_number\":\"1\"", json);
        Assert.Contains("\"quantity\":1", json);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<TableInfo>(json)));
    }

    [Fact]
    public void A_balloon_says_its_item_and_component()
    {
        var info = new AnnotationInfo { Name = "DetailItem7", Kind = AnnotationKind.Balloon, Sheet = "Assembly", View = "Drawing View9", ItemNumber = "2", Component = "pin-1" };
        string json = JsonCodec.Serialize(info);
        Assert.Contains("\"kind\":\"balloon\"", json);
        Assert.Contains("\"item_number\":\"2\"", json);
        Assert.Contains("\"component\":\"pin-1\"", json);
    }

    [Fact]
    public void Drawing_changes_count_tables()
    {
        string json = JsonCodec.Serialize(new DrawingChange { TablesBefore = 0, TablesAfter = 1 });
        Assert.Contains("\"tables_before\":0", json);
        Assert.Contains("\"tables_after\":1", json);
    }

    [Fact]
    public void An_exploded_image_gives_the_move_and_the_boxes()
    {
        var image = new ExplodedImage
        {
            Name = "pin-1", Moved = new SheetPoint(0, 40),
            Expected = new SheetBox { Min = new SheetPoint(85, 190), Max = new SheetPoint(95, 220) },
            Drawn = new SheetBox { Min = new SheetPoint(85, 190), Max = new SheetPoint(95, 220) },
        };
        string json = JsonCodec.Serialize(image);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<ExplodedImage>(json)));
        Assert.Contains("\"moved\":{\"x\":0,\"y\":40}", json);
    }
}
