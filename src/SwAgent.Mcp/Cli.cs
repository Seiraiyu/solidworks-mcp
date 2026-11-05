using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Registration;

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

    /// <summary>How swmcp asks a running SOLIDWORKS to load the add-in; a fake in tests.</summary>
    public ISolidWorksAttach SolidWorks { get; init; } = ComSolidWorksAttach.Instance;

    /// <summary>How long <c>addin load</c> waits for the add-in's pipe after LoadAddIn returns.</summary>
    public TimeSpan AddinLoadWait { get; init; } = TimeSpan.FromSeconds(30);
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
        "  swmcp call <method> [json-params] [--pid N] [--deadline-ms N]\n" +
        "  swmcp addin register [--path <SwAgent.AddIn.dll>]\n" +
        "  swmcp addin load [--pid N]\n" +
        "  swmcp addin unregister\n" +
        "  swmcp addin status\n" +
        "  swmcp --version";

    public static async Task<int> RunAsync(string[] args, CliEnvironment env)
    {
        try
        {
            if (args.Length == 0) throw new UsageException("no command given");
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
                case "call":
                    return await CallAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
                case "addin":
                    return await AddinAsync(args.Skip(1).ToArray(), env).ConfigureAwait(false);
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

        using var client = await SwAgentClient.ConnectAsync(env.Client).ConfigureAwait(false);
        TimeSpan? deadline = deadlineMs == null ? null : TimeSpan.FromMilliseconds(deadlineMs.Value);
        object? result = await client.CallAsync(method, parameters, deadline).ConfigureAwait(false);
        Write(env, result);
        return ExitCodes.Success;
    }

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

    /// <summary>
    /// Asks the running SOLIDWORKS to load the registered add-in (SOLIDWORKS does not discover per-user add-ins at
    /// startup), then waits for its pipe. Attaches only: SOLIDWORKS is started by the user, never by swmcp.
    /// </summary>
    static async Task<int> LoadAsync(AddinRegistrar registrar, Args a, CliEnvironment env)
    {
        if (a.Positional.Count > 0) throw new UsageException("addin load takes no arguments besides --pid");
        int? wanted = a.Int("--pid") ?? env.Client.ProcessId;

        var status = registrar.Status();
        if (!status.Registered || status.Path == null)
        {
            throw new SwAgentException(ErrorCode.AddinNotLoaded,
                status.Path != null && !status.FileExists
                    ? $"the add-in is registered at {status.Path}, which no longer exists; run `swmcp addin register` first"
                    : "the add-in is not registered for this user; run `swmcp addin register` first",
                new JsonObject { ["registered_path"] = status.Path, ["file_exists"] = status.FileExists });
        }

        int pid = new AddinLocator(env.Client.Processes, env.Client.Pipes, env.Client.PipeNameFor).LocateProcess(wanted);
        string pipe = env.Client.PipeNameFor(pid);
        if (env.Client.Pipes.Exists(pipe))
        {
            Write(env, new JsonObject { ["loaded"] = true, ["already_loaded"] = true, ["process_id"] = (long)pid, ["path"] = status.Path });
            return ExitCodes.Success;
        }

        int result = env.SolidWorks.LoadAddIn(pid, status.Path);
        var deadline = DateTime.UtcNow + env.AddinLoadWait;
        while (!env.Client.Pipes.Exists(pipe))
        {
            if (DateTime.UtcNow >= deadline)
            {
                string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "logs");
                throw new SwAgentException(ErrorCode.AddinNotLoaded,
                    $"SOLIDWORKS (process {pid}) did not start the add-in: LoadAddIn returned {result} and \\\\.\\pipe\\{pipe} " +
                    $"did not appear within {env.AddinLoadWait.TotalSeconds:0.#} s; the add-in's log is in {logs}",
                    new JsonObject
                    {
                        ["process_id"] = (long)pid,
                        ["path"] = status.Path,
                        ["load_addin_result"] = (long)result,
                        ["log_directory"] = logs,
                    });
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Write(env, new JsonObject
        {
            ["loaded"] = true,
            ["already_loaded"] = false,
            ["process_id"] = (long)pid,
            ["path"] = status.Path,
            ["load_addin_result"] = (long)result,
        });
        return ExitCodes.Success;
    }

    static List<(int ProcessId, bool AddinLoaded)> RunningSolidWorks(CliEnvironment env) =>
        env.Client.Processes.SolidWorksProcessIds()
            .Select(pid => (pid, env.Client.Pipes.Exists(env.Client.PipeNameFor(pid))))
            .ToList();

    static void Write(CliEnvironment env, object? json) => env.Out.WriteLine(Json.Write(json, indented: true));

    sealed class Args
    {
        public List<string> Positional { get; } = new List<string>();

        public Dictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public static Args Parse(string[] args, params string[] allowed)
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
