using System.Linq;
using SwAgent.Contracts;
using SwAgent.Mcp.Server;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class PatternToolTests
{
    [Theory]
    [InlineData("sw_component_linear_pattern", false)]
    [InlineData("sw_component_circular_pattern", false)]
    [InlineData("sw_component_feature_pattern", false)]
    [InlineData("sw_assembly_interference", true)]
    public void Each_operation_is_one_tool(string name, bool readOnly)
    {
        var tool = Assert.Single(ToolCatalog.All, t => t.Name == name);
        Assert.Equal(readOnly, tool.ReadOnly);
    }

    [Fact]
    public void Diag_interference_is_no_tool() => Assert.DoesNotContain(ToolCatalog.All, t => t.Name == "sw_diag_interference");

    [Theory]
    [InlineData("component.linear_pattern")]
    [InlineData("component.circular_pattern")]
    [InlineData("component.feature_pattern")]
    public void Pattern_tools_say_units_reference_forms_and_rollback(string method)
    {
        string d = OperationCatalog.Find(method)!.Description;
        Assert.Contains("mm", d);
        Assert.Contains("name@component", d);
        Assert.Contains("rolled back", d);
    }

    [Fact]
    public void The_interference_tool_says_its_units_and_that_the_session_is_closed()
    {
        string d = OperationCatalog.Find("assembly.interference")!.Description;
        Assert.Contains("mm³", d);
        Assert.Contains("always closed", d);
    }
}
