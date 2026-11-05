using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using SwAgent.AddIn.Core.Pipe;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PipeServerTests
{
    static PipeServer Echoing(string name, MemoryLog log, int maxBytes = PipeServer.DefaultMaxMessageBytes)
    {
        var server = new PipeServer(name, (line, reply) => reply.Send("echo:" + line), log, maxBytes);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Answers_lines_on_the_connection_they_came_from()
    {
        string name = PipeNames.Unique();
        using var server = Echoing(name, new MemoryLog());
        using var client = new RawClient(name);
        await client.SendAsync("a");
        await client.SendAsync("b");
        Assert.Equal("echo:a", await client.ReadLineAsync());
        Assert.Equal("echo:b", await client.ReadLineAsync());
    }

    [Fact]
    public void A_second_server_cannot_take_the_name()
    {
        string name = PipeNames.Unique();
        using var server = Echoing(name, new MemoryLog());
        using var squatter = new PipeServer(name, (l, r) => { }, new MemoryLog());
        Assert.Throws<PipeNameInUseException>(() => squatter.Start());
    }

    [Fact]
    public async Task A_second_client_is_answered_busy_and_closed()
    {
        string name = PipeNames.Unique();
        using var server = Echoing(name, new MemoryLog());
        using var first = new RawClient(name);
        await first.SendAsync("hold");
        Assert.Equal("echo:hold", await first.ReadLineAsync());

        using var second = new RawClient(name);
        var busy = RpcCodec.ParseResponse((await second.ReadLineAsync())!);
        Assert.Equal(ErrorCode.Busy, busy.Error!.Code);
        Assert.Equal((long)Process.GetCurrentProcess().Id, busy.Error.Details!["holder_process_id"]);
        Assert.Null(await second.ReadLineAsync());

        await first.SendAsync("still");
        Assert.Equal("echo:still", await first.ReadLineAsync());
    }

    [Fact]
    public async Task The_next_client_is_served_after_the_first_leaves()
    {
        string name = PipeNames.Unique();
        var log = new MemoryLog();
        using var server = Echoing(name, log);
        using (var first = new RawClient(name))
        {
            await first.SendAsync("one");
            Assert.Equal("echo:one", await first.ReadLineAsync());
        }
        log.WaitFor("disconnected");
        using var next = new RawClient(name);
        await next.SendAsync("two");
        Assert.Equal("echo:two", await next.ReadLineAsync());
    }

    [Fact]
    public async Task An_oversized_message_is_refused_and_the_connection_closed()
    {
        string name = PipeNames.Unique();
        using var server = Echoing(name, new MemoryLog(), maxBytes: 1024);
        using var client = new RawClient(name);
        await client.SendAsync(new string('x', 2000));
        var refused = RpcCodec.ParseResponse((await client.ReadLineAsync())!);
        Assert.Equal(ErrorCode.InvalidArgument, refused.Error!.Code);
        Assert.Equal(1024L, refused.Error.Details!["max_bytes"]);
        Assert.Null(await client.ReadLineAsync());
    }

    [Fact]
    public async Task The_reply_channel_closes_when_the_client_leaves()
    {
        string name = PipeNames.Unique();
        IReplyChannel? channel = null;
        using var server = new PipeServer(name, (line, reply) => { channel = reply; reply.Send("ok"); }, new MemoryLog());
        server.Start();
        using (var client = new RawClient(name))
        {
            await client.SendAsync("x");
            Assert.Equal("ok", await client.ReadLineAsync());
            Assert.True(channel!.IsOpen);
        }
        var until = DateTime.UtcNow.AddSeconds(10);
        while (channel.IsOpen && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.False(channel.IsOpen);
    }

    [Fact]
    public void Only_the_current_user_may_open_the_pipe_and_never_over_the_network()
    {
        var rules = NativePipe.BuildSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        var allow = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Allow);
        Assert.Equal(WindowsIdentity.GetCurrent().User, allow.IdentityReference);
        var deny = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Deny);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), deny.IdentityReference);
    }
}
