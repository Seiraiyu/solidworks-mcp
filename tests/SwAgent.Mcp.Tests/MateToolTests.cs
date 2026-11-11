using System.Linq;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class MateToolTests
{
    [Theory]
    [InlineData("sw_mate_add", false)]
    [InlineData("sw_mate_edit", false)]
    [InlineData("sw_mate_delete", false)]
    [InlineData("sw_mate_list", true)]
    [InlineData("sw_assembly_dof", true)]
    public void Each_mate_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Fact]
    public void The_mate_tool_says_its_units_its_entity_forms_and_its_rollback()
    {
        var tool = ToolCatalog.All.Single(t => t.Name == "sw_mate_add");
        var add = SwAgent.Contracts.OperationCatalog.Find(tool.Operation!)!.Description;
        Assert.Contains("mm", add);
        Assert.Contains("name@component", add);
        Assert.Contains("put back", add);
    }
}
