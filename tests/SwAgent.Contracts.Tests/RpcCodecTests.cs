using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class RpcCodecTests
{
    [Fact]
    public void Formats_a_request_on_one_line()
    {
        var request = new RpcRequest(7, "session.status", new JsonObject(), 30000);
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"session.status\",\"params\":{},\"deadline_ms\":30000}",
            RpcCodec.FormatRequest(request));
    }

    [Fact]
    public void Parses_what_it_formats()
    {
        var parsed = RpcCodec.ParseRequest(RpcCodec.FormatRequest(new RpcRequest(9, "test.x", new JsonObject { ["a"] = 1L }, 500)));
        Assert.Equal(9, parsed.Id);
        Assert.Equal("test.x", parsed.Method);
        Assert.Equal(1L, parsed.Params["a"]);
        Assert.Equal(500, parsed.DeadlineMs);
    }

    [Fact]
    public void Absent_params_are_an_empty_object()
    {
        var parsed = RpcCodec.ParseRequest("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"m.x\",\"deadline_ms\":5}");
        Assert.Empty(parsed.Params);
    }

    [Theory]
    [InlineData("nope", RpcErrorCodes.ParseError, null)]
    [InlineData("[]", RpcErrorCodes.InvalidRequest, null)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"m.x\",\"deadline_ms\":5}", RpcErrorCodes.InvalidRequest, null)]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":5}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"\",\"deadline_ms\":5}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\"}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":0}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":3600001}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":9223372036854775807}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":5,\"params\":[]}", RpcErrorCodes.InvalidRequest, 3L)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"m.x\",\"deadline_ms\":5,\"extra\":1}", RpcErrorCodes.InvalidRequest, 3L)]
    public void Bad_requests_are_refused_with_the_right_code(string line, int rpcCode, long? id)
    {
        var e = Assert.Throws<RpcParseException>(() => RpcCodec.ParseRequest(line));
        Assert.Equal(rpcCode, e.RpcCode);
        Assert.Equal(id, e.Id);
        Assert.Equal(ErrorCode.InvalidArgument, e.Error.Code);
    }

    [Fact]
    public void Formats_success_and_failure()
    {
        Assert.Equal("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"a\":1}}", RpcCodec.Success(7, new JsonObject { ["a"] = 1L }));
        var error = new SwAgentError(ErrorCode.Timeout, "slow\nline", new JsonObject { ["started"] = false });
        string line = RpcCodec.Failure(null, RpcErrorCodes.ServerError, error);
        Assert.Equal(
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32000,\"message\":\"slow\\nline\",\"data\":{\"code\":\"timeout\",\"message\":\"slow\\nline\",\"details\":{\"started\":false}}}}",
            line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Parses_responses_back_to_typed_errors()
    {
        var ok = RpcCodec.ParseResponse(RpcCodec.Success(4, "x"));
        Assert.True(ok.IsSuccess);
        Assert.Equal(4L, ok.Id);
        Assert.Equal("x", ok.Result);

        var failed = RpcCodec.ParseResponse(RpcCodec.Failure(5, RpcErrorCodes.InvalidParams, new SwAgentError(ErrorCode.InvalidArgument, "bad")));
        Assert.False(failed.IsSuccess);
        Assert.Equal(5L, failed.Id);
        Assert.Equal(RpcErrorCodes.InvalidParams, failed.RpcCode);
        Assert.Equal(ErrorCode.InvalidArgument, failed.Error!.Code);
        Assert.Equal("bad", failed.Error.Message);
    }

    [Fact]
    public void A_deadline_of_one_hour_is_accepted()
    {
        Assert.Equal(3_600_000L, RpcCodec.ParseRequest("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"m.x\",\"deadline_ms\":3600000}").DeadlineMs);
    }
}
