using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

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
