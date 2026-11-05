using System;
using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class JsonTests
{
    [Fact]
    public void Parses_scalars()
    {
        Assert.True(Json.Parse("true") is true);
        Assert.Null(Json.Parse(" null "));
        Assert.Equal(42L, Json.Parse("42"));
        Assert.Equal(-1.5, Json.Parse("-1.5"));
        Assert.Equal(1000.0, Json.Parse("1e3"));
        Assert.Equal("a\nb\u00e9/", Json.Parse("\"a\\nb\\u00e9\\/\""));
        Assert.IsType<double>(Json.Parse("12345678901234567890"));
    }

    [Fact]
    public void Objects_keep_property_order()
    {
        var o = Assert.IsType<JsonObject>(Json.Parse("{\"b\":1,\"a\":[true,null,\"x\"]}"));
        Assert.Equal(new[] { "b", "a" }, o.Keys);
        var a = Assert.IsType<List<object?>>(o["a"]);
        Assert.Equal(new object?[] { true, null, "x" }, a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("01")]
    [InlineData("\"\t\"")]
    [InlineData("nul")]
    [InlineData("1 2")]
    [InlineData("NaN")]
    [InlineData("-")]
    [InlineData("1.")]
    [InlineData("1e400")]
    [InlineData("\"\\x\"")]
    public void Rejects_invalid_json(string text)
    {
        Assert.Throws<JsonParseException>(() => Json.Parse(text));
    }

    [Fact]
    public void Rejects_nesting_deeper_than_the_limit()
    {
        string deep = new string('[', Json.MaxDepth + 2) + new string(']', Json.MaxDepth + 2);
        Assert.Throws<JsonParseException>(() => Json.Parse(deep));
    }

    [Fact]
    public void Writer_escapes_so_a_message_stays_on_one_line()
    {
        string written = Json.Write("a\"b\\c\nd\r\u0001\u2028\u00e9");
        Assert.Equal("\"a\\\"b\\\\c\\nd\\r\\u0001\\u2028\u00e9\"", written);
        Assert.DoesNotContain("\n", written);
        Assert.Equal("a\"b\\c\nd\r\u0001\u2028\u00e9", Json.Parse(written));
    }

    [Fact]
    public void Compact_round_trip_is_stable()
    {
        const string text = "{\"n\":null,\"b\":false,\"i\":-7,\"d\":0.25,\"s\":\"x\",\"a\":[1,[2],{}],\"o\":{\"k\":[]}}";
        Assert.Equal(text, Json.Write(Json.Parse(text)));
    }

    [Fact]
    public void Indented_output_uses_two_spaces_and_newlines()
    {
        var o = new JsonObject { ["a"] = 1L, ["b"] = new List<object?> { "x", true }, ["c"] = new JsonObject(), ["d"] = new List<object?>() };
        const string expected = "{\n  \"a\": 1,\n  \"b\": [\n    \"x\",\n    true\n  ],\n  \"c\": {},\n  \"d\": []\n}";
        Assert.Equal(expected, Json.Write(o, indented: true));
    }

    [Fact]
    public void Writer_refuses_values_json_cannot_hold()
    {
        Assert.Throws<ArgumentException>(() => Json.Write(double.NaN));
        Assert.Throws<ArgumentException>(() => Json.Write(new DateTime(2026, 1, 1)));
    }
}
