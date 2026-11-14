using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class DrawingToolTests
{
    [Theory]
    [InlineData("sw_drawing_read", true)]
    [InlineData("sw_sheet_add", false)]
    [InlineData("sw_sheet_set", false)]
    [InlineData("sw_sheet_activate", false)]
    [InlineData("sw_sheet_delete", false)]
    [InlineData("sw_view_create", false)]
    [InlineData("sw_view_project", false)]
    [InlineData("sw_view_set", false)]
    [InlineData("sw_view_delete", false)]
    [InlineData("sw_view_entities", true)]
    [InlineData("sw_view_section", false)]
    [InlineData("sw_view_detail", false)]
    [InlineData("sw_view_flat_pattern", false)]
    public void Each_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Theory]
    [InlineData("drawing.read")]
    [InlineData("sheet.add")]
    [InlineData("sheet.set")]
    [InlineData("view.create")]
    [InlineData("view.project")]
    [InlineData("view.set")]
    [InlineData("view.entities")]
    [InlineData("view.section")]
    [InlineData("view.detail")]
    [InlineData("view.flat_pattern")]
    public void Positions_and_sizes_are_said_to_be_sheet_mm(string operation)
    {
        Assert.Contains("sheet mm", OperationCatalog.Find(operation)!.Description);
    }

    [Theory]
    [InlineData("view.section", "cut nothing")]
    [InlineData("view.detail", "draws nothing")]
    [InlineData("view.flat_pattern", "sheetmetal.read")]
    public void Sections_details_and_flat_patterns_say_model_mm_what_they_check_and_the_rollback(string operation, string check)
    {
        string d = OperationCatalog.Find(operation)!.Description;
        Assert.Contains("model mm", d);
        Assert.Contains("within 0.01 mm", d);
        Assert.Contains("rolled back", d);
        Assert.Contains(check, d);
    }

    [Fact]
    public void View_flat_pattern_states_the_saved_model_rule_and_the_sheet_metal_rule()
    {
        string d = OperationCatalog.Find("view.flat_pattern")!.Description;
        Assert.Contains("sw_document_save_as", d);
        Assert.Contains("needs sheet_metal", d);
        Assert.Contains("marked_dirty", d);
    }

    [Fact]
    public void View_create_states_the_saved_model_rule_what_it_checks_and_the_rollback()
    {
        string d = OperationCatalog.Find("view.create")!.Description;
        Assert.Contains("sw_document_save_as", d);
        Assert.Contains("within 0.01 mm", d);
        Assert.Contains("rolled back", d);
    }

    [Fact]
    public void View_entities_says_model_mm_for_the_model_and_sheet_mm_for_the_sheet()
    {
        string d = OperationCatalog.Find("view.entities")!.Description;
        Assert.Contains("model mm", d);
        Assert.Contains("sheet mm", d);
        Assert.Contains("r1.edge", d);
    }

    [Fact]
    public void Document_new_says_it_makes_drawings()
    {
        Assert.Contains("drawing", OperationCatalog.Find("document.new")!.Description);
        Assert.DoesNotContain("later release", OperationCatalog.Find("document.new")!.Description);
    }

    [Fact]
    public void Deletes_say_they_are_not_rolled_back_and_why()
    {
        Assert.Contains("Not rolled back", OperationCatalog.Find("sheet.delete")!.Description);
        Assert.Contains("Not rolled back", OperationCatalog.Find("view.delete")!.Description);
    }
}
