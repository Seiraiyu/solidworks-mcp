using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class AnnotationToolTests
{
    [Theory]
    [InlineData("sw_annotation_import", false)]
    [InlineData("sw_annotation_dimension", false)]
    [InlineData("sw_annotation_note", false)]
    [InlineData("sw_annotation_centre_marks", false)]
    [InlineData("sw_annotation_centre_lines", false)]
    [InlineData("sw_annotation_datum", false)]
    [InlineData("sw_annotation_gtol", false)]
    [InlineData("sw_annotation_list", true)]
    [InlineData("sw_annotation_set", false)]
    [InlineData("sw_annotation_delete", false)]
    [InlineData("sw_drawing_properties", false)]
    public void Each_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Theory]
    [InlineData("annotation.import")]
    [InlineData("annotation.dimension")]
    [InlineData("annotation.note")]
    [InlineData("annotation.centre_marks")]
    [InlineData("annotation.datum")]
    [InlineData("annotation.gtol")]
    [InlineData("annotation.list")]
    [InlineData("annotation.set")]
    public void Positions_are_said_to_be_sheet_mm(string operation)
    {
        Assert.Contains("sheet mm", OperationCatalog.Find(operation)!.Description);
    }

    [Theory]
    [InlineData("annotation.dimension")]
    [InlineData("annotation.note")]
    [InlineData("annotation.centre_marks")]
    [InlineData("annotation.centre_lines")]
    [InlineData("annotation.datum")]
    [InlineData("annotation.gtol")]
    public void What_attaches_to_the_model_takes_view_entities_selected_through_the_view_never_sheet_coordinates(string operation)
    {
        string d = OperationCatalog.Find(operation)!.Description;
        Assert.Contains("ViewEntity", d);
        Assert.Contains("never picked by sheet coordinates", d);
    }

    [Theory]
    [InlineData("annotation.import")]
    [InlineData("annotation.dimension")]
    [InlineData("annotation.note")]
    [InlineData("annotation.centre_marks")]
    [InlineData("annotation.centre_lines")]
    [InlineData("annotation.datum")]
    [InlineData("annotation.gtol")]
    [InlineData("annotation.set")]
    [InlineData("drawing.properties")]
    public void Every_change_says_what_it_checks_and_that_it_is_rolled_back(string operation)
    {
        string d = OperationCatalog.Find(operation)!.Description;
        Assert.Contains("Checked", d);
        Assert.True(d.Contains("rolled back") || d.Contains("put back"), operation);
    }

    [Theory]
    [InlineData("annotation.import")]
    [InlineData("annotation.dimension")]
    [InlineData("annotation.set")]
    public void Model_dimension_values_are_changed_on_the_model_with_dimension_set(string operation)
    {
        Assert.Contains("dimension.set", OperationCatalog.Find(operation)!.Description);
    }

    [Fact]
    public void Dimensions_say_model_mm_and_their_tolerance()
    {
        foreach (var operation in new[] { "annotation.import", "annotation.dimension" })
        {
            string d = OperationCatalog.Find(operation)!.Description;
            Assert.Contains("model mm", d);
            Assert.Contains("within 0.001 mm", d);
        }
    }

    [Fact]
    public void Delete_says_it_is_not_rolled_back_and_that_the_model_keeps_its_dimensions()
    {
        string d = OperationCatalog.Find("annotation.delete")!.Description;
        Assert.Contains("Not rolled back", d);
        Assert.Contains("the model keeps it", d);
    }

    [Fact]
    public void Properties_say_how_they_are_read_and_what_is_not_edited()
    {
        string d = OperationCatalog.Find("drawing.properties")!.Description;
        Assert.Contains("Get2", d);
        Assert.Contains("$PRP", d);
        Assert.Contains("not the model's", d);
    }
}
