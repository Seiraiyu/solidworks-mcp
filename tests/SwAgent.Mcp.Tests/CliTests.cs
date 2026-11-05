using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Rpc;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class CliTests
{
    sealed class Run
    {
        public int Exit;
        public string Out = "";
        public string Error = "";
        public JsonObject Json => (JsonObject)Contracts.Serialization.Json.Parse(Out)!;
    }

    static async Task<Run> RunAsync(CliEnvironmentBuilder env, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await Cli.RunAsync(args, env.Build(stdout, stderr));
        return new Run { Exit = exit, Out = stdout.ToString(), Error = stderr.ToString() };
    }

    sealed class CliEnvironmentBuilder
    {
        public Client.ClientOptions Client { get; init; } = new Client.ClientOptions { Processes = new FakeProcesses(), Pipes = new FakePipes(_ => false) };
        public Microsoft.Win32.RegistryKey? Root { get; init; }

        public CliEnvironment Build(TextWriter stdout, TextWriter stderr) => new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            Client = Client,
            // Tests that do not register get a read-only key, so a slip cannot write to the real HKCU.
            RegistryRoot = Root ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: false)!,
            BaseDirectory = AppContext.BaseDirectory,
        };
    }

    [Fact]
    public void Every_error_code_has_its_own_exit_code()
    {
        var codes = Enum.GetValues<ErrorCode>().Select(ExitCodes.For).ToList();
        Assert.DoesNotContain(ExitCodes.Internal, codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public async Task No_command_is_a_usage_error()
    {
        var run = await RunAsync(new CliEnvironmentBuilder());
        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("usage:", run.Error);
    }

    [Fact]
    public async Task Call_prints_the_result_and_exits_zero()
    {
        using var addin = new FakeAddin();
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "call", "session.status", "{}", "--deadline-ms", "5000");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("34.4.0", run.Json["sw_revision"]);
        addin.Next();
        Assert.Equal(5000L, addin.Next().DeadlineMs);
    }

    [Fact]
    public async Task Call_prints_a_typed_error_and_exits_with_its_code()
    {
        using var addin = new FakeAddin(r => RpcCodec.Failure(r.Id, RpcErrorCodes.ServerError, new SwAgentError(ErrorCode.OperationFailed, "it broke")));
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "call", "session.status");
        Assert.Equal(ExitCodes.For(ErrorCode.OperationFailed), run.Exit);
        Assert.Equal("operation_failed", run.Json["code"]);
        Assert.Equal("it broke", run.Json["message"]);
    }

    [Fact]
    public async Task Call_without_SOLIDWORKS_exits_sw_not_running()
    {
        var run = await RunAsync(new CliEnvironmentBuilder(), "call", "session.status");
        Assert.Equal(ExitCodes.For(ErrorCode.SwNotRunning), run.Exit);
        Assert.Equal("sw_not_running", run.Json["code"]);
    }

    [Theory]
    [InlineData("call")]
    [InlineData("call", "session.status", "[1]")]
    [InlineData("call", "session.status", "{bad")]
    [InlineData("call", "session.status", "--deadline-ms", "0")]
    [InlineData("call", "session.status", "--pid", "x")]
    [InlineData("call", "session.status", "--what", "1")]
    [InlineData("addin")]
    [InlineData("addin", "frobnicate")]
    [InlineData("nonsense")]
    public async Task Bad_command_lines_are_usage_errors(params string[] args)
    {
        var run = await RunAsync(new CliEnvironmentBuilder(), args);
        Assert.Equal(ExitCodes.Usage, run.Exit);
    }

    [Fact]
    public async Task Addin_register_status_unregister_round_trip()
    {
        using var scratch = new ScratchRegistry();
        var env = new CliEnvironmentBuilder { Root = scratch.Root };

        var status = await RunAsync(env, "addin", "status");
        Assert.Equal(ExitCodes.NotRegistered, status.Exit);
        Assert.Equal(false, status.Json["registered"]);

        var register = await RunAsync(env, "addin", "register");
        Assert.Equal(ExitCodes.Success, register.Exit);
        Assert.Equal(Path.GetFullPath(Payload.AddinPath), register.Json["path"]);

        status = await RunAsync(env, "addin", "status");
        Assert.Equal(ExitCodes.Success, status.Exit);
        Assert.Equal(true, status.Json["registered"]);

        var unregister = await RunAsync(env, "addin", "unregister");
        Assert.Equal(ExitCodes.Success, unregister.Exit);
        Assert.Equal(4, ((System.Collections.IList)unregister.Json["removed_keys"]!).Count);
    }

    [Fact]
    public async Task Addin_register_refuses_a_path_that_is_not_the_addin()
    {
        using var scratch = new ScratchRegistry();
        var run = await RunAsync(new CliEnvironmentBuilder { Root = scratch.Root }, "addin", "register", "--path", typeof(Cli).Assembly.Location);
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), run.Exit);
        Assert.Equal("invalid_argument", run.Json["code"]);
    }

    [Fact]
    public async Task Addin_register_says_to_restart_when_SOLIDWORKS_has_it_loaded()
    {
        using var scratch = new ScratchRegistry();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(77), Pipes = new FakePipes(_ => true) },
        };
        var run = await RunAsync(env, "addin", "register");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("restart SOLIDWORKS", (string)run.Json["next"]!);
    }
}
