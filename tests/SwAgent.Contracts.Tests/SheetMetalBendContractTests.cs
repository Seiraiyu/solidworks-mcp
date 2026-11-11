using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class SheetMetalBendContractTests
{
    /// <summary>The names in a request's schema that it requires.</summary>
    internal static List<string> Required(System.Type request) =>
        JsonSchema.For(request).TryGetValue("required", out var list) ? ((List<object?>)list!).Select(v => (string)v!).ToList() : new List<string>();

    /// <summary>An operation of the sheetmetal family that changes the part and is an MCP tool named after it.</summary>
    internal static void IsSheetMetalChange(string name, System.Type request, System.Type result)
    {
        var op = OperationCatalog.Find(name)!;
        Assert.Equal("sheetmetal", op.Family);
        Assert.Equal(request, op.RequestType);
        Assert.Equal(result, op.ResultType);
        Assert.False(op.ReadOnly);
        Assert.False(op.Internal);
        Assert.Equal("sw_" + name.Replace('.', '_'), op.ToolName);
    }

    [Fact]
    public void Protocol_8_speaks_bends_and_flattening()
    {
        Assert.Equal(8, Protocol.Version);
    }
}
