using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class DrawingExportToolTests
{
    [Fact]
    public void Export_drawing_is_one_tool_that_changes_something()
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == "sw_export_drawing");
        Assert.False(tool.ReadOnly);
        Assert.Equal("export.drawing", tool.Operation);
    }

    [Fact]
    public void Its_description_gives_units_files_checks_and_the_saved_drawing_rule()
    {
        string d = OperationCatalog.Find("export.drawing")!.Description;
        Assert.Contains("sheet mm", d);
        Assert.Contains("<name>-<sheet>.dxf", d);
        Assert.Contains("never saved is refused before SOLIDWORKS is called", d);
        Assert.Contains("sw_document_save_as", d);
        Assert.Contains("Checked on disk", d);
        Assert.Contains("one page per sheet", d);
        Assert.Contains("marked_dirty", d);
        Assert.Contains("not saved behind you", d);
        Assert.Contains("put back", d);
    }
}
