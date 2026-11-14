using System;
using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class DrawingExportContractTests
{
    [Fact]
    public void Protocol_17_speaks_drawing_export()
    {
        Assert.Equal(17, Protocol.Version);
    }

    [Fact]
    public void The_drawing_export_is_an_export_and_an_MCP_tool()
    {
        var op = OperationCatalog.Find("export.drawing")!;
        Assert.Equal("export", op.Family);
        Assert.Equal(typeof(ExportDrawingRequest), op.RequestType);
        Assert.Equal(typeof(ExportDrawingResult), op.ResultType);
        Assert.False(op.ReadOnly);
        Assert.False(op.Internal);
        Assert.Equal("sw_export_drawing", op.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(120), OperationFamilies.DefaultFor("export.drawing", TimeSpan.Zero));
    }

    [Fact]
    public void It_needs_a_path_and_a_format_and_nothing_else()
    {
        Assert.Equal(new[] { "path", "format" }, SheetMetalBendContractTests.Required(typeof(ExportDrawingRequest)).ToArray());
    }

    [Theory]
    [InlineData(DrawingExportFormat.Pdf, "pdf")]
    [InlineData(DrawingExportFormat.Dxf, "dxf")]
    [InlineData(DrawingExportFormat.Dwg, "dwg")]
    public void Its_formats_have_lower_case_wire_names(DrawingExportFormat format, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(format));
    }

    [Fact]
    public void Its_request_names_sheets_by_reference_or_name()
    {
        string json = JsonCodec.Serialize(new ExportDrawingRequest { Path = @"C:\out\block.pdf", Format = DrawingExportFormat.Pdf, Sheets = new List<string> { "Sheet1", "r1.sheet.x" } });
        Assert.Contains("\"format\":\"pdf\"", json);
        Assert.Contains("\"sheets\":[\"Sheet1\",\"r1.sheet.x\"]", json);
        var back = JsonCodec.Deserialize<ExportDrawingRequest>("{\"path\":\"C:\\\\out\\\\block.dxf\",\"format\":\"dxf\"}");
        Assert.Equal(DrawingExportFormat.Dxf, back.Format);
        Assert.Null(back.Sheets);
        Assert.Null(back.Overwrite);
    }

    [Fact]
    public void Its_result_gives_each_file_with_its_sheets_pages_and_the_view_found_in_it()
    {
        var result = new ExportDrawingResult
        {
            Format = DrawingExportFormat.Dxf,
            Sheets = { "Sheet1" },
            MarkedDirty = true,
            Files =
            {
                new DrawingExportFile
                {
                    Path = @"C:\out\block-Sheet1.dxf",
                    Sheets = { "Sheet1" },
                    Bytes = 12000,
                    Header = "DXF",
                    Extents = new DrawingExtents { MinXMm = 0, MinYMm = 0, MaxXMm = 420, MaxYMm = 297, LengthMm = 420, WidthMm = 297, From = "its entities" },
                    View = "Drawing View1",
                    ViewExtents = new SheetBox { Min = new SheetPoint(90, 160), Max = new SheetPoint(150, 200) },
                },
            },
        };
        string json = JsonCodec.Serialize(result);
        Assert.Contains("\"marked_dirty\":true", json);
        Assert.Contains("\"view\":\"Drawing View1\"", json);
        Assert.Contains("\"view_extents\":{", json);
        Assert.Contains("\"pages\":[]", json);

        var pdf = new DrawingExportFile { Path = @"C:\out\block.pdf", Header = "%PDF-1.4" };
        pdf.Pages.Add(new ExportedPage { Sheet = "Sheet1", WidthMm = 420, HeightMm = 297 });
        Assert.Contains("\"pages\":[{\"sheet\":\"Sheet1\",\"width_mm\":420,\"height_mm\":297}]", JsonCodec.Serialize(pdf));
    }
}
