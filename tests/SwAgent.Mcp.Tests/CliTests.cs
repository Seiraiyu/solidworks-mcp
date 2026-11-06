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
        // Tests that do not attach get one that fails the test, so a slip cannot reach the real SOLIDWORKS.
        public Client.ISolidWorksAttach SolidWorks { get; init; } = new FakeAttach((_, _) => throw new InvalidOperationException("this test must not attach to SOLIDWORKS"));

        // The --pid of every serve the command line started (null for none); the MCP server never runs for real here.
        public System.Collections.Generic.List<int?> Served { get; } = new System.Collections.Generic.List<int?>();

        // Whether each serve offered the escape hatch.
        public System.Collections.Generic.List<bool> ServedApiCall { get; } = new System.Collections.Generic.List<bool>();

        // The environment the command line reads (SWMCP_API_CALL); empty unless a test sets it.
        public System.Collections.Generic.Dictionary<string, string> Variables { get; } = new System.Collections.Generic.Dictionary<string, string>();

        public CliEnvironment Build(TextWriter stdout, TextWriter stderr) => new CliEnvironment
        {
            Out = stdout,
            Error = stderr,
            Client = Client,
            // Tests that do not register get a read-only key, so a slip cannot write to the real HKCU.
            RegistryRoot = Root ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software", writable: false)!,
            BaseDirectory = AppContext.BaseDirectory,
            SolidWorks = SolidWorks,
            AddinLoadWait = TimeSpan.FromMilliseconds(300),
            Serve = options => { Served.Add(options.Client.ProcessId); ServedApiCall.Add(options.ApiCall); return Task.FromResult(ExitCodes.Success); },
            Variable = name => Variables.TryGetValue(name, out var value) ? value : null,
            Processes = new FakeProcessWatch(),
            Stop = Stop,
        };

        /// <summary>Ends events --follow.</summary>
        public System.Threading.CancellationToken Stop { get; init; }
    }

    [Fact]
    public async Task Call_loads_the_addin_when_SOLIDWORKS_runs_without_it()
    {
        using var scratch = Registered();
        using var addin = new FakeAddin();
        bool up = false;
        var options = addin.Options();
        options.Pipes = new FakePipes(n => up && n == addin.PipeName);
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = options,
            SolidWorks = new FakeAttach((_, _) => { up = true; return 0; }),
        };
        var run = await RunAsync(env, "call", "session.status");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("34.4.0", run.Json["sw_revision"]);
    }

    [Fact]
    public async Task Serve_offers_the_escape_hatch_unless_the_flag_or_the_variable_turns_it_off()
    {
        var on = new CliEnvironmentBuilder();
        Assert.Equal(ExitCodes.Success, (await RunAsync(on, "serve")).Exit);
        Assert.Equal(ExitCodes.Success, (await RunAsync(on)).Exit);
        Assert.Equal(new[] { true, true }, on.ServedApiCall);

        var flag = new CliEnvironmentBuilder();
        Assert.Equal(ExitCodes.Success, (await RunAsync(flag, "serve", "--no-api-call", "--pid", "42")).Exit);
        Assert.Equal(new[] { false }, flag.ServedApiCall);
        Assert.Equal(new int?[] { 42 }, flag.Served);

        foreach (var value in new[] { "0", "false", "OFF", " no " })
        {
            var variable = new CliEnvironmentBuilder();
            variable.Variables["SWMCP_API_CALL"] = value;
            await RunAsync(variable, "serve");
            Assert.Equal(new[] { false }, variable.ServedApiCall);
        }
        var stillOn = new CliEnvironmentBuilder();
        stillOn.Variables["SWMCP_API_CALL"] = "1";
        await RunAsync(stillOn, "serve");
        Assert.Equal(new[] { true }, stillOn.ServedApiCall);
    }

    [Fact]
    public async Task Call_api_call_is_refused_when_the_escape_hatch_is_off_and_SOLIDWORKS_is_not_touched()
    {
        var env = new CliEnvironmentBuilder();
        env.Variables["SWMCP_API_CALL"] = "0";
        var run = await RunAsync(env, "call", "api.call", "{\"target\":\"app\",\"member\":\"RevisionNumber\"}");
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), run.Exit);
        Assert.Equal("invalid_argument", run.Json["code"]);
        Assert.Equal("SWMCP_API_CALL", ((JsonObject)run.Json["details"]!)["switch"]);
    }

    [Fact]
    public async Task Status_without_SOLIDWORKS_prints_not_running_and_exits_zero()
    {
        var run = await RunAsync(new CliEnvironmentBuilder(), "status");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("not_running", run.Json["state"]);
    }

    [Fact]
    public async Task Status_prints_what_sw_status_answers()
    {
        using var addin = new FakeAddin();
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "status");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("ready", run.Json["state"]);
        Assert.Equal("0.1.0-fake", ((JsonObject)run.Json["addin"]!)["addin_version"]);
    }

    [Fact]
    public async Task Events_prints_the_events_the_addin_holds_after_a_cursor()
    {
        using var addin = new FakeAddin();
        addin.Held.Add(new EventEnvelope { Seq = 1, Kind = "document.opened", Time = "t1", Data = new JsonObject() });
        addin.Held.Add(new EventEnvelope { Seq = 2, Kind = "document.saved", Time = "t2", Data = new JsonObject() });
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "events", "--after-seq", "1");
        Assert.Equal(ExitCodes.Success, run.Exit);
        var e = (JsonObject)Assert.Single((System.Collections.Generic.List<object?>)run.Json["events"]!)!;
        Assert.Equal("document.saved", e["kind"]);
        Assert.Equal(1L, addin.Next("addin.events").Params["after_seq"]);
    }

    [Fact]
    public async Task Events_follow_prints_a_line_per_event_until_stopped()
    {
        using var addin = new FakeAddin();
        addin.Held.Add(new EventEnvelope { Seq = 1, Kind = "document.opened", Time = "t1", Data = new JsonObject() });
        using var stop = new System.Threading.CancellationTokenSource();
        var stdout = new StringWriter();
        var env = new CliEnvironmentBuilder { Client = addin.Options(), Stop = stop.Token };
        var running = Cli.RunAsync(new[] { "events", "--follow" }, env.Build(stdout, new StringWriter()));
        addin.Next("addin.events");
        await addin.SendEventAsync(2, "document.saved", new DocumentEvent { Title = "Part1" });
        for (int i = 0; i < 250 && !stdout.ToString().Contains("document.saved"); i++) await Task.Delay(20);
        stop.Cancel();
        Assert.Equal(ExitCodes.Success, await running.WaitAsync(TimeSpan.FromSeconds(10)));
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "document.opened", "document.saved" }, lines.Select(l => (string)((JsonObject)Contracts.Serialization.Json.Parse(l)!)["kind"]!).ToArray());
    }

    [Fact]
    public void Every_error_code_has_its_own_exit_code()
    {
        var codes = Enum.GetValues<ErrorCode>().Select(ExitCodes.For).ToList();
        Assert.DoesNotContain(ExitCodes.Internal, codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public async Task No_command_serves()
    {
        var env = new CliEnvironmentBuilder();
        var run = await RunAsync(env);
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Null(Assert.Single(env.Served));
        Assert.Empty(run.Out);
    }

    [Fact]
    public async Task Serve_takes_a_pid()
    {
        var env = new CliEnvironmentBuilder();
        var run = await RunAsync(env, "serve", "--pid", "4242");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(4242, Assert.Single(env.Served));
    }

    [Fact]
    public async Task Help_prints_the_usage()
    {
        var run = await RunAsync(new CliEnvironmentBuilder(), "--help");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("swmcp [serve]", run.Out);
    }

    [Fact]
    public async Task Call_prints_the_result_and_exits_zero()
    {
        using var addin = new FakeAddin();
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "call", "session.status", "{}", "--deadline-ms", "5000");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal("34.4.0", run.Json["sw_revision"]);
        Assert.Equal(5000L, addin.Next("session.status").DeadlineMs);
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
    [InlineData("addin", "load", "--pid", "x")]
    [InlineData("addin", "load", "extra")]
    [InlineData("nonsense")]
    [InlineData("serve", "extra")]
    [InlineData("serve", "--pid", "0")]
    [InlineData("status", "extra")]
    [InlineData("events", "--after-seq", "x")]
    [InlineData("events", "--follow", "extra")]
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

    /// <summary>A scratch registration of the real add-in DLL.</summary>
    static ScratchRegistry Registered()
    {
        var scratch = new ScratchRegistry();
        new Registration.AddinRegistrar(scratch.Root).Register(Payload.AddinPath);
        return scratch;
    }

    [Fact]
    public async Task Addin_register_says_to_run_addin_load_when_SOLIDWORKS_does_not_have_it()
    {
        using var scratch = new ScratchRegistry();
        var run = await RunAsync(new CliEnvironmentBuilder { Root = scratch.Root }, "addin", "register");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("swmcp addin load", (string)run.Json["next"]!);
    }

    [Fact]
    public async Task Addin_load_without_SOLIDWORKS_is_sw_not_running()
    {
        using var scratch = Registered();
        var run = await RunAsync(new CliEnvironmentBuilder { Root = scratch.Root }, "addin", "load");
        Assert.Equal(ExitCodes.For(ErrorCode.SwNotRunning), run.Exit);
        Assert.Equal("sw_not_running", run.Json["code"]);
    }

    [Fact]
    public async Task Addin_load_with_two_instances_and_no_pid_is_sw_multiple_instances()
    {
        using var scratch = Registered();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(10, 20), Pipes = new FakePipes(_ => false) },
        };
        var run = await RunAsync(env, "addin", "load");
        Assert.Equal(ExitCodes.For(ErrorCode.SwMultipleInstances), run.Exit);
    }

    [Fact]
    public async Task Addin_load_loads_the_registered_dll_into_the_named_instance_and_waits_for_its_pipe()
    {
        using var scratch = Registered();
        bool up = false;
        (int Pid, string Path)? asked = null;
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(10, 20), Pipes = new FakePipes(n => up && n == "solidworks-mcp-20") },
            SolidWorks = new FakeAttach((pid, path) => { asked = (pid, path); up = true; return 0; }),
        };
        var run = await RunAsync(env, "addin", "load", "--pid", "20");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal((20, Path.GetFullPath(Payload.AddinPath)), asked);
        Assert.Equal(true, run.Json["loaded"]);
        Assert.Equal(false, run.Json["already_loaded"]);
        Assert.Equal(20L, run.Json["process_id"]);
    }

    [Fact]
    public async Task Addin_load_when_the_pipe_is_up_does_not_attach()
    {
        using var scratch = Registered();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(10), Pipes = new FakePipes(_ => true) },
        };
        var run = await RunAsync(env, "addin", "load");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Equal(true, run.Json["already_loaded"]);
    }

    [Fact]
    public async Task Addin_load_when_not_registered_is_addin_not_loaded_without_attaching()
    {
        using var scratch = new ScratchRegistry();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(10), Pipes = new FakePipes(_ => false) },
        };
        var run = await RunAsync(env, "addin", "load");
        Assert.Equal(ExitCodes.For(ErrorCode.AddinNotLoaded), run.Exit);
        Assert.Contains("swmcp addin register", (string)run.Json["message"]!);
    }

    [Fact]
    public async Task Addin_load_whose_pipe_never_appears_is_addin_not_loaded_with_the_result_code()
    {
        using var scratch = Registered();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(10), Pipes = new FakePipes(_ => false) },
            SolidWorks = new FakeAttach((_, _) => 2),
        };
        var run = await RunAsync(env, "addin", "load");
        Assert.Equal(ExitCodes.For(ErrorCode.AddinNotLoaded), run.Exit);
        Assert.Equal(2L, ((JsonObject)run.Json["details"]!)["load_addin_result"]);
    }

    [Fact]
    public async Task Addin_unregister_while_loaded_says_to_unregister_again_after_the_restart()
    {
        using var scratch = Registered();
        var env = new CliEnvironmentBuilder
        {
            Root = scratch.Root,
            Client = new Client.ClientOptions { Processes = new FakeProcesses(77), Pipes = new FakePipes(_ => true) },
        };
        var run = await RunAsync(env, "addin", "unregister");
        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains("run `swmcp addin unregister` again", (string)run.Json["next"]!);
    }

    [Fact]
    public async Task Call_refuses_a_deadline_over_one_hour()
    {
        using var addin = new FakeAddin();
        var run = await RunAsync(new CliEnvironmentBuilder { Client = addin.Options() }, "call", "session.status", "{}", "--deadline-ms", "3600001");
        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), run.Exit);
        Assert.Equal("invalid_argument", run.Json["code"]);
    }

    [Fact]
    public void Sw_starting_has_its_own_exit_code()
    {
        Assert.Equal(15, ExitCodes.For(ErrorCode.SwStarting));
    }
}
