using Xunit;

namespace SwAgent.Contracts.Tests;

public class WireNamesTests
{
    enum Sample { FirstValue, [WireName("custom")] Second }

    [Theory]
    [InlineData("ProtocolVersion", "protocol_version")]
    [InlineData("SwRevision", "sw_revision")]
    [InlineData("Id", "id")]
    [InlineData("HTTPServer", "http_server")]
    [InlineData("Sw3dPrinter", "sw3d_printer")]
    [InlineData("already_snake", "already_snake")]
    public void SnakeCase_converts_pascal_case(string input, string expected)
    {
        Assert.Equal(expected, WireNames.SnakeCase(input));
    }

    [Fact]
    public void Enum_members_use_snake_case_or_their_wire_name()
    {
        Assert.Equal("first_value", WireNames.ForEnum(Sample.FirstValue));
        Assert.Equal("custom", WireNames.ForEnum(Sample.Second));
        Assert.Equal(new[] { "first_value", "custom" }, WireNames.EnumNames(typeof(Sample)));
    }

    [Fact]
    public void Enum_parsing_accepts_only_wire_names()
    {
        Assert.True(WireNames.TryParseEnum(typeof(Sample), "custom", out var value));
        Assert.Equal(Sample.Second, value);
        Assert.False(WireNames.TryParseEnum(typeof(Sample), "Second", out _));
    }
}
