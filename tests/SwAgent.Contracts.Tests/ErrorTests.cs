using System;
using System.Linq;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class ErrorTests
{
    [Fact]
    public void Error_codes_have_their_released_wire_names()
    {
        // These names are a public contract: never rename one, only add.
        var expected = new[]
        {
            "sw_not_running", "sw_crashed", "sw_multiple_instances", "addin_not_loaded", "addin_incompatible",
            "timeout", "busy", "dialog_open", "invalid_argument", "reference_unresolved", "wrong_document_type",
            "operation_failed", "verification_failed", "api_error",
        };
        Assert.Equal(expected, WireNames.EnumNames(typeof(ErrorCode)).ToArray());
    }

    [Fact]
    public void Exception_carries_the_typed_error()
    {
        var details = new JsonObject { ["field"] = "x" };
        var e = new SwAgentException(ErrorCode.InvalidArgument, "x is wrong", details);
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("x is wrong", e.Message);
        Assert.Same(details, e.Error.Details);
        Assert.Equal("invalid_argument: x is wrong", e.Error.ToString());
    }
}
