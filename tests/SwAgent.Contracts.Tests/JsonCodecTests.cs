using System;
using System.Collections.Generic;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public enum Shade { LightGrey, Dark }

public sealed class Inner
{
    [Required] public string Name { get; set; } = "";
    public double? Size { get; set; }
}

public sealed class Sample
{
    public int Count { get; set; }
    public long Big { get; set; }
    public bool Flag { get; set; }
    public double Ratio { get; set; }
    public Shade Shade { get; set; }
    public string? Note { get; set; }
    public int? Maybe { get; set; }
    public Inner? Inner { get; set; }
    public List<Inner>? Items { get; set; }
    public JsonObject? Extra { get; set; }
}

public sealed class Unsupported
{
    public decimal Amount { get; set; }
}

public sealed class Recursive
{
    public Recursive? Next { get; set; }
}

public class JsonCodecTests
{
    const string Minimal = "\"count\":1,\"big\":1,\"flag\":false,\"ratio\":1,\"shade\":\"dark\"";

    static SwAgentException Invalid(string json) =>
        Assert.Throws<SwAgentException>(() => JsonCodec.Deserialize<Sample>(json));

    [Fact]
    public void Serializes_snake_case_enum_strings_and_omits_nulls()
    {
        var sample = new Sample { Count = 3, Big = 1L << 40, Flag = true, Ratio = 0.5, Shade = Shade.LightGrey, Inner = new Inner { Name = "a" } };
        Assert.Equal(
            "{\"count\":3,\"big\":1099511627776,\"flag\":true,\"ratio\":0.5,\"shade\":\"light_grey\",\"inner\":{\"name\":\"a\"}}",
            JsonCodec.Serialize(sample));
    }

    [Fact]
    public void Round_trips_every_supported_kind()
    {
        var sample = new Sample
        {
            Count = -2, Big = long.MaxValue, Flag = true, Ratio = 1.25, Shade = Shade.Dark, Note = "line\nbreak é", Maybe = 7,
            Inner = new Inner { Name = "x", Size = 2.5 },
            Items = new List<Inner> { new Inner { Name = "p" }, new Inner { Name = "q", Size = 0 } },
            Extra = new JsonObject { ["k"] = new List<object?> { 1L, "two" } },
        };
        string once = JsonCodec.Serialize(sample);
        string twice = JsonCodec.Serialize(JsonCodec.Deserialize<Sample>(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Integers_are_accepted_where_a_double_is_expected()
    {
        Assert.Equal(1.0, JsonCodec.Deserialize<Sample>("{" + Minimal + "}").Ratio);
    }

    [Fact]
    public void Unknown_property_is_named()
    {
        var e = Invalid("{" + Minimal + ",\"bogus\":1}");
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("bogus", e.Error.Details!["field"]);
        Assert.Contains("not a known property of Sample", e.Message);
    }

    [Fact]
    public void Missing_required_value_is_named()
    {
        var e = Invalid("{}");
        Assert.Equal("count", e.Error.Details!["field"]);
        Assert.Contains("is required", e.Message);
    }

    [Fact]
    public void Wrong_type_is_named()
    {
        var e = Invalid("{\"count\":\"x\",\"big\":1,\"flag\":false,\"ratio\":1,\"shade\":\"dark\"}");
        Assert.Equal("count", e.Error.Details!["field"]);
        Assert.Contains("must be an integer", e.Message);
    }

    [Fact]
    public void Nested_and_list_paths_are_named()
    {
        Assert.Equal("inner.name", Invalid("{" + Minimal + ",\"inner\":{}}").Error.Details!["field"]);
        Assert.Equal("items[1].name", Invalid("{" + Minimal + ",\"items\":[{\"name\":\"a\"},{\"name\":5}]}").Error.Details!["field"]);
        Assert.Equal("inner.name", Invalid("{" + Minimal + ",\"inner\":{\"name\":null}}").Error.Details!["field"]);
    }

    [Fact]
    public void Bad_enum_lists_the_choices()
    {
        var e = Invalid("{\"count\":1,\"big\":1,\"flag\":false,\"ratio\":1,\"shade\":\"Dark\"}");
        Assert.Contains("light_grey, dark", e.Message);
    }

    [Fact]
    public void Int_overflow_is_invalid()
    {
        var e = Invalid("{\"count\":3000000000,\"big\":1,\"flag\":false,\"ratio\":1,\"shade\":\"dark\"}");
        Assert.Equal("count", e.Error.Details!["field"]);
    }

    [Fact]
    public void Malformed_json_is_invalid_argument_with_a_position()
    {
        var e = Invalid("{\"count\":");
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.IsType<long>(e.Error.Details!["position"]);
    }

    [Fact]
    public void Unsupported_and_recursive_contract_types_are_refused()
    {
        Assert.Throws<NotSupportedException>(() => JsonCodec.Serialize(new Unsupported()));
        Assert.Throws<NotSupportedException>(() => JsonCodec.Serialize(new Recursive()));
    }

    [Fact]
    public void Error_round_trips_through_the_codec()
    {
        var error = new SwAgentError(ErrorCode.Timeout, "slow", new JsonObject { ["started"] = false });
        Assert.Equal("{\"code\":\"timeout\",\"message\":\"slow\",\"details\":{\"started\":false}}", JsonCodec.Serialize(error));
        var back = JsonCodec.Deserialize<SwAgentError>(JsonCodec.Serialize(error));
        Assert.Equal(ErrorCode.Timeout, back.Code);
        Assert.True(back.Details!["started"] is false);
    }
}
