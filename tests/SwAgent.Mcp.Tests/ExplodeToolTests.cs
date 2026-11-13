using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class ExplodeToolTests
{
    [Theory]
    [InlineData("sw_explode_create", false)]
    [InlineData("sw_explode_list", true)]
    [InlineData("sw_explode_show", false)]
    [InlineData("sw_explode_delete", false)]
    [InlineData("sw_export_assembly", false)]
    public void Each_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Fact]
    public void Explode_create_says_units_reference_forms_and_rollback()
    {
        string d = OperationCatalog.Find("explode.create")!.Description;
        Assert.Contains("mm", d);
        Assert.Contains("degrees", d);
        Assert.Contains("name@component", d);
        Assert.Contains("rolled back", d);
    }

    [Fact]
    public void Export_assembly_says_what_it_checks_and_sends_parts_to_export_part()
    {
        string d = OperationCatalog.Find("export.assembly")!.Description;
        Assert.Contains("mm", d);
        Assert.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", d);
        Assert.Contains("export.part writes parts", d);
    }

    [Fact]
    public void Arm_dialog_reaches_assembly_changes() => Assert.Contains("assembly change", OperationCatalog.Find("diag.arm_dialog")!.Description);
}
