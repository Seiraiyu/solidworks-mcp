using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Registration;
using SwAgent.Mcp.Install;
using SwAgent.Mcp.Server;

namespace SwAgent.Mcp;

/// <summary>Everything the command line touches outside itself, so tests can substitute each part.</summary>
public sealed class CliEnvironment
{
    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public ClientOptions Client { get; init; } = new ClientOptions();

    /// <summary>HKCU in use; a scratch key in tests.</summary>
    public RegistryKey RegistryRoot { get; init; } = Registry.CurrentUser;

    /// <summary>Where swmcp.exe is; the add-in is expected in its addin\ folder.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>%LOCALAPPDATA%, under which install puts solidworks-mcp\app; a scratch folder in tests.</summary>
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>How swmcp asks a running SOLIDWORKS to load the add-in; a fake in tests.</summary>
    public ISolidWorksAttach SolidWorks { get; init; } = ComSolidWorksAttach.Instance;

    /// <summary>How long <c>addin load</c> waits for the add-in's pipe after LoadAddIn returns.</summary>
    public TimeSpan AddinLoadWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tells when a SOLIDWORKS process exits; a fake in tests.</summary>
    public IProcessWatch Processes { get; init; } = SystemProcessWatch.Instance;

    /// <summary>Runs the MCP server on stdio until the client disconnects; a fake in tests.</summary>
    public Func<SupervisorOptions, Task<int>> Serve { get; init; } = options => McpHost.RunAsync(options);

    /// <summary>Ends <c>events --follow</c> (Ctrl+C).</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Reads an environment variable (SWMCP_API_CALL); a dictionary in tests.</summary>
    public Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;
}

/// <summary>The escape hatch's switch (design 371): on unless <c>SWMCP_API_CALL</c> is 0, false, off or no.</summary>
public static class ApiCallSwitch
{
    public const string Variable = "SWMCP_API_CALL";

    public const string Flag = "--no-api-call";

    public static bool Enabled(string? value)
    {
        string v = (value ?? "").Trim();
        return !(v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}

/// <summary>swmcp's command line. Every command prints one JSON document on stdout and exits per <see cref="ExitCodes"/>.</summary>
public static class Cli
{
    public const string UsageText =
        "usage:\n" +
        "  swmcp [serve] [--pid N] [--no-api-call]   MCP server on stdio (the default); --no-api-call (or SWMCP_API_CALL=0) leaves out sw_api_call\n" +
        "  swmcp call <method> [json-params] [--pid N] [--deadline-ms N]\n" +
        "  swmcp status [--pid N]\n" +
        "  swmcp events [--after-seq N] [--follow] [--pid N]\n" +
        "  swmcp addin register [--path <SwAgent.AddIn.dll>]\n" +
        "  swmcp addin load [--pid N]\n" +
        "  swmcp addin unregister\n" +
        "  swmcp addin status\n" +
        "  swmcp install                 from an unzipped release: copies it to %LOCALAPPDATA%\\solidworks-mcp\\app\\<version> and registers its add-in\n" +
        "  swmcp uninstall               unregisters the add-in and removes the installed versions\n" +
        "  swmcp --version";

    public static async Task<int> RunAsync(string[] args, CliEnvironment env)
    {
        try
        {
            if (args.Length == 0) return await ServeAsync(args, env).ConfigureAwait(false);
            switch (args[0])
            {
                case "help":
                case "--help":
                case "-h":
                    env.Out.WriteLine(UsageText);
                    return ExitCodes.Success;
                case "version":
                case "--version":
                    env.Out.WriteLine(SwAgentClient.Version);
                    return ExitCodes.Success;
                case "serve":
                    return await ServeAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "call":
                    return await CallAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "status":
                    return await StatusAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "events":
                    return await EventsAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "addin":
                    return await AddinAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "install":
                    return Install(args.Skip(1).ToArray(), env);
                case "uninstall":
                    return Uninstall(args.Skip(1).ToArray(), env);
                default:
                    throw new UsageException($"unknown command '{args[0]}'");
            }
        }
        catch (UsageException e)
        {
            env.Error.WriteLine("swmcp: " + e.Message);
            env.Error.WriteLine(UsageText);
            return ExitCodes.Usage;
        }
        catch (SwAgentException e)
        {
            Write(env, JsonCodec.ToJson(e.Error));
            return ExitCodes.For(e.Code);
        }
        catch (Exception e)
        {
            env.Error.WriteLine("swmcp: unexpected failure: " + e);
            return ExitCodes.Internal;
        }
    }

    static async Task<int> ServeAsync(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args, new[] { ApiCallSwitch.Flag }, "--pid");
        if (a.Positional.Count > 0) throw new UsageException("serve takes no arguments besides --pid and --no-api-call");
        int? pid = a.Int("--pid");
        if (pid != null) env.Client.ProcessId = pid;
        var options = Options(env);
        if (a.Flags.Contains(ApiCallSwitch.Flag)) options.ApiCall = false;
        return await env.Serve(options).ConfigureAwait(false);
    }

    static async Task<int> CallAsync(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args, "--pid", "--deadline-ms");
        if (a.Positional.Count < 1 || a.Positional.Count > 2) throw new UsageException("call needs a method and, optionally, a JSON object of params");
        string method = a.Positional[0];
        var parameters = new JsonObject();
        if (a.Positional.Count == 2)
        {
            object? tree;
            try
            {
                tree = Json.Parse(a.Positional[1]);
            }
            catch (JsonParseException e)
            {
                throw new UsageException($"the params are not valid JSON: {e.Message}");
            }
            parameters = tree as JsonObject ?? throw new UsageException("the params must be a JSON object");
        }
        int? pid = a.Int("--pid");
        int? deadlineMs = a.Int("--deadline-ms");
        if (pid != null) env.Client.ProcessId = pid;

        using var supervisor = new Supervisor(Options(env));
        TimeSpan? deadline = deadlineMs == null ? null : TimeSpan.FromMilliseconds(deadlineMs.Value);
        object? result = await supervisor.CallAsync(method, parameters, deadline).ConfigureAwait(false);
        Write(env, result);
        return ExitCodes.Success;
    }

    /// <summary>What sw_status answers, from a terminal. Always exits 0: a SOLIDWORKS that is absent or starting is a state.</summary>
    static async Task<int> StatusAsync(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args, "--pid");
        if (a.Positional.Count > 0) throw new UsageException("status takes no arguments besides --pid");
        int? pid = a.Int("--pid");
        if (pid != null) env.Client.ProcessId = pid;
        using var supervisor = new Supervisor(Options(env));
        Write(env, JsonCodec.ToJson(await supervisor.StatusAsync().ConfigureAwait(false)));
        return ExitCodes.Success;
    }

    /// <summary>
    /// The events the add-in still holds (after --after-seq, in the add-in's own numbering) as one JSON document; with
    /// --follow, each held event and then each new one as a line of JSON, until Ctrl+C.
    /// </summary>
    static async Task<int> EventsAsync(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args, new[] { "--follow" }, "--pid", "--after-seq");
        if (a.Positional.Count > 0) throw new UsageException("events takes no arguments besides --after-seq, --follow and --pid");
        int? pid = a.Int("--pid");
        if (pid != null) env.Client.ProcessId = pid;
        var request = new AddinEventsRequest { AfterSeq = a.Int("--after-seq") };

        using var supervisor = new Supervisor(Options(env));
        if (!a.Flags.Contains("--follow"))
        {
            Write(env, JsonCodec.ToJson(await supervisor.CallAsync<AddinEventsRequest, AddinEventsResult>(request).ConfigureAwait(false)));
            return ExitCodes.Success;
        }

        using var live = new BlockingCollection<EventEnvelope>();
        supervisor.EventReceived += live.Add;
        var held = await supervisor.CallAsync<AddinEventsRequest, AddinEventsResult>(request).ConfigureAwait(false);
        long printed = request.AfterSeq ?? 0;
        foreach (var e in held.Events) printed = Line(env, e);
        try
        {
            while (true)
            {
                var e = live.Take(env.Stop);
                if (e.Seq > printed || e.Seq == 1) printed = Line(env, e); // seq 1 again: the add-in was loaded again
            }
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
    }

    static long Line(CliEnvironment env, EventEnvelope e)
    {
        env.Out.WriteLine(Json.Write(JsonCodec.ToJson(e)));
        env.Out.Flush();
        return e.Seq;
    }

    /// <summary>The supervisor every command that reaches SOLIDWORKS uses, so each loads the add-in the same way.</summary>
    static SupervisorOptions Options(CliEnvironment env) => new SupervisorOptions
    {
        Client = env.Client,
        SolidWorks = env.SolidWorks,
        Registration = new RegistrarAddinRegistration(new AddinRegistrar(env.RegistryRoot)),
        Processes = env.Processes,
        PipeWait = env.AddinLoadWait,
        ApiCall = ApiCallSwitch.Enabled(env.Variable(ApiCallSwitch.Variable)),
    };

    static async Task<int> AddinAsync(string[] args, CliEnvironment env)
    {
        if (args.Length == 0) throw new UsageException("addin needs register, load, unregister or status");
        var registrar = new AddinRegistrar(env.RegistryRoot);
        var rest = args.Skip(1).ToArray();
        switch (args[0])
        {
            case "register":
            {
                var a = Args.Parse(rest, "--path");
                if (a.Positional.Count > 0) throw new UsageException("addin register takes no arguments besides --path");
                string path = a.Options.TryGetValue("--path", out var p)
                    ? p
                    : Path.Combine(env.BaseDirectory, "addin", AddInIdentity.AssemblyFileName);
                var assembly = registrar.Register(path);
                var loaded = RunningSolidWorks(env).Where(s => s.AddinLoaded).Select(s => s.ProcessId).ToList();
                Write(env, new JsonObject
                {
                    ["registered"] = true,
                    ["path"] = assembly.Path,
                    ["assembly"] = assembly.FullName,
                    ["clsid"] = AddInIdentity.ClsidKey,
                    ["next"] = loaded.Count > 0
                        ? $"SOLIDWORKS (process {string.Join(", ", loaded)}) has the add-in loaded and its DLL locked; restart SOLIDWORKS to load this build"
                        : "run `swmcp addin load` once SOLIDWORKS is running (start it from the 3DEXPERIENCE launcher)",
                });
                return ExitCodes.Success;
            }
            case "load":
                return await LoadAsync(registrar, Args.Parse(rest, "--pid"), env).ConfigureAwait(false);
            case "unregister":
            {
                var a = Args.Parse(rest);
                if (a.Positional.Count > 0) throw new UsageException("addin unregister takes no arguments");
                var removed = registrar.Unregister();
                bool loaded = RunningSolidWorks(env).Any(s => s.AddinLoaded);
                Write(env, new JsonObject
                {
                    ["unregistered"] = true,
                    ["removed_keys"] = removed.Select(k => (object?)k).ToList(),
                    // SOLIDWORKS writes AddInsStartup for every add-in it has loaded when it exits, so that key comes back once.
                    ["next"] = loaded
                        ? "restart SOLIDWORKS to unload the add-in, then run `swmcp addin unregister` again: SOLIDWORKS rewrites the add-in's AddInsStartup entry when it exits"
                        : "nothing; SOLIDWORKS will not load the add-in",
                });
                return ExitCodes.Success;
            }
            case "status":
            {
                var a = Args.Parse(rest);
                if (a.Positional.Count > 0) throw new UsageException("addin status takes no arguments");
                var status = registrar.Status();
                Write(env, new JsonObject
                {
                    ["registered"] = status.Registered,
                    ["path"] = status.Path,
                    ["file_exists"] = status.FileExists,
                    ["keys"] = new JsonObject
                    {
                        ["clsid"] = status.ClsidKey,
                        ["prog_id"] = status.ProgIdKey,
                        ["addins"] = status.AddInsKey,
                        ["startup"] = status.StartupKey,
                    },
                    ["solidworks"] = RunningSolidWorks(env)
                        .Select(s => (object?)new JsonObject { ["process_id"] = (long)s.ProcessId, ["addin_loaded"] = s.AddinLoaded })
                        .ToList(),
                });
                return status.Registered ? ExitCodes.Success : ExitCodes.NotRegistered;
            }
            default:
                throw new UsageException($"unknown addin command '{args[0]}'");
        }
    }

    /// <summary>The line that adds the installed server to Claude Code, for every project.</summary>
    public static string ClaudeLine(string swmcp) => $"claude mcp add --scope user solidworks -- \"{swmcp}\" serve";

    /// <summary>
    /// swmcp install (design 371), run from the unzipped release: copies it to its version folder, removes the Mark of
    /// the Web, registers that copy's add-in under HKCU and writes current.txt. Needs no administrator rights.
    /// </summary>
    static int Install(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args);
        if (a.Positional.Count > 0) throw new UsageException("install takes no arguments");
        string version = InstallLayout.FolderName(SwAgentClient.Version);
        var result = new Installer(new InstallLayout(env.LocalAppData), new AddinRegistrar(env.RegistryRoot)).Install(env.BaseDirectory, version);
        var loaded = RunningSolidWorks(env).Where(s => s.AddinLoaded).Select(s => s.ProcessId).ToList();
        Write(env, new JsonObject
        {
            ["installed"] = true,
            ["version"] = result.Version,
            ["path"] = result.Directory,
            ["swmcp"] = result.Swmcp,
            ["addin"] = result.Addin.Path,
            ["files_copied"] = (long)result.FilesCopied,
            ["marks_of_the_web_removed"] = (long)result.MarksRemoved,
            ["claude_code"] = ClaudeLine(result.Swmcp),
            ["next"] = loaded.Count > 0
                ? $"SOLIDWORKS (process {string.Join(", ", loaded)}) has an earlier add-in loaded; this one loads after you close SOLIDWORKS and start it again from the 3DEXPERIENCE dashboard. Then run the claude_code line"
                : "run the claude_code line, then start SOLIDWORKS from the 3DEXPERIENCE dashboard; the first tool call loads the add-in",
        });
        return ExitCodes.Success;
    }

    /// <summary>swmcp uninstall: removes the registration and every version folder except the one it runs from.</summary>
    static int Uninstall(string[] args, CliEnvironment env)
    {
        var a = Args.Parse(args);
        if (a.Positional.Count > 0) throw new UsageException("uninstall takes no arguments");
        var result = new Installer(new InstallLayout(env.LocalAppData), new AddinRegistrar(env.RegistryRoot)).Uninstall(env.BaseDirectory);
        bool loaded = RunningSolidWorks(env).Any(s => s.AddinLoaded);
        Write(env, new JsonObject
        {
            ["uninstalled"] = true,
            ["removed_keys"] = result.RemovedKeys.Select(k => (object?)k).ToList(),
            ["removed"] = result.Removed.Select(d => (object?)d).ToList(),
            ["kept"] = result.Kept.Select(k => (object?)new JsonObject { ["path"] = k.Directory, ["reason"] = k.Reason }).ToList(),
            ["claude_code"] = "claude mcp remove --scope user solidworks",
            ["next"] = loaded
                ? "close SOLIDWORKS, then run swmcp uninstall again: SOLIDWORKS holds the add-in's files and rewrites its AddInsStartup entry when it exits"
                : result.Kept.Count > 0
                    ? "delete the folders under kept once this has exited, and run the claude_code line"
                    : "run the claude_code line; nothing of solidworks-mcp is registered any more",
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Asks the running SOLIDWORKS to load the registered add-in (SOLIDWORKS does not discover per-user add-ins at
    /// startup), then waits for its pipe: the same loader the supervisor uses. Attaches only: SOLIDWORKS is started by
    /// the user, never by swmcp.
    /// </summary>
    static async Task<int> LoadAsync(AddinRegistrar registrar, Args a, CliEnvironment env)
    {
        if (a.Positional.Count > 0) throw new UsageException("addin load takes no arguments besides --pid");
        int? wanted = a.Int("--pid") ?? env.Client.ProcessId;
        var loader = Loader(env, new RegistrarAddinRegistration(registrar));
        string path = loader.RegisteredPath(); // said before looking for SOLIDWORKS: registering is the first thing to fix

        int pid = new AddinLocator(env.Client.Processes, env.Client.Pipes, env.Client.PipeNameFor).LocateProcess(wanted);
        var loaded = await loader.EnsureLoadedAsync(pid, DateTime.UtcNow + env.AddinLoadWait + TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["loaded"] = true,
            ["already_loaded"] = loaded.AlreadyLoaded,
            ["process_id"] = (long)pid,
            ["path"] = path,
        };
        if (loaded.LoadAddInResult != null) result["load_addin_result"] = (long)loaded.LoadAddInResult.Value;
        Write(env, result);
        return ExitCodes.Success;
    }

    static AddinLoader Loader(CliEnvironment env, IAddinRegistration registration) =>
        new AddinLoader(env.SolidWorks, registration, env.Client.Pipes, env.Client.PipeNameFor, env.Client.Logger) { PipeWait = env.AddinLoadWait };

    static List<(int ProcessId, bool AddinLoaded)> RunningSolidWorks(CliEnvironment env) =>
        env.Client.Processes.SolidWorksProcessIds()
            .Select(pid => (pid, env.Client.Pipes.Exists(env.Client.PipeNameFor(pid))))
            .ToList();

    static void Write(CliEnvironment env, object? json) => env.Out.WriteLine(Json.Write(json, indented: true));

    sealed class Args
    {
        public List<string> Positional { get; } = new List<string>();

        public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The flags given (options that take no value).</summary>
        public HashSet<string> Flags { get; } = new HashSet<string>(StringComparer.Ordinal);

        public static Args Parse(string[] args, params string[] allowed) => Parse(args, Array.Empty<string>(), allowed);

        public static Args Parse(string[] args, string[] flags, params string[] allowed)
        {
            var parsed = new Args();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    parsed.Positional.Add(arg);
                    continue;
                }
                if (flags.Contains(arg))
                {
                    parsed.Flags.Add(arg);
                    continue;
                }
                if (!allowed.Contains(arg)) throw new UsageException($"unknown option '{arg}'");
                if (i + 1 >= args.Length) throw new UsageException($"{arg} needs a value");
                parsed.Options[arg] = args[++i];
            }
            return parsed;
        }

        /// <summary>A positive integer option, or null when absent.</summary>
        public int? Int(string name)
        {
            if (!Options.TryGetValue(name, out var text)) return null;
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) || value < 1)
                throw new UsageException($"{name} must be a positive integer, not '{text}'");
            return value;
        }
    }
}
