using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class AssemblyDrawingToolTests
{
    [Theory]
    [InlineData("sw_table_bom", false)]
    [InlineData("sw_table_read", true)]
    [InlineData("sw_table_delete", false)]
    [InlineData("sw_annotation_balloons", false)]
    [InlineData("sw_view_entities", true)]
    public void Each_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Theory]
    [InlineData("table.bom")]
    [InlineData("annotation.balloons")]
    public void Changes_say_what_they_check_and_that_they_are_rolled_back(string operation)
    {
        string d = OperationCatalog.Find(operation)!.Description;
        Assert.Contains("Checked", d);
        Assert.Contains("rolled back", d);
        Assert.Contains("not by SOLIDWORKS' answer", d);
        Assert.Contains("active one", d);
    }

    [Fact]
    public void The_bill_names_its_configuration_and_checks_rows_against_assembly_components()
    {
        string d = OperationCatalog.Find("table.bom")!.Description;
        Assert.Contains("configuration named, never empty", d);
        Assert.Contains("assembly.components", d);
        Assert.Contains("no rows is verification_failed", d);
    }

    [Fact]
    public void Balloons_select_the_view_and_number_to_the_bill()
    {
        string d = OperationCatalog.Find("annotation.balloons")!.Description;
        Assert.Contains("never picked by sheet coordinates", d);
        Assert.Contains("bill of materials", d);
    }

    [Fact]
    public void Table_delete_says_it_is_not_rolled_back()
    {
        Assert.Contains("Not rolled back", OperationCatalog.Find("table.delete")!.Description);
    }

    [Fact]
    public void Exploded_views_say_how_they_are_checked_and_point_at_explode_create()
    {
        foreach (var operation in new[] { "view.create", "view.set" })
        {
            var op = OperationCatalog.Find(operation)!;
            Assert.Contains("exploded", op.Description);
            Assert.Contains("active one", Describe(op.RequestType, "Exploded"));
        }
        Assert.Contains("explode.create", OperationCatalog.Find("view.create")!.Description);
        Assert.Contains("sw_explode_create", Describe(typeof(ViewCreateRequest), "Exploded"));
    }

    [Fact]
    public void View_entities_reads_assembly_views_component_by_component()
    {
        string d = OperationCatalog.Find("view.entities")!.Description;
        Assert.Contains("component by component", d);
        Assert.DoesNotContain("refused", d);
    }

    static string Describe(System.Type type, string property) =>
        ((System.ComponentModel.DescriptionAttribute)type.GetProperty(property)!.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)[0]).Description;
}
