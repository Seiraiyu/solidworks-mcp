using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SwAgent.Contracts.Serialization;

namespace SwAgent.Live.Tests;

/// <summary>One tool call the transcript made and what came back.</summary>
public sealed record TranscriptCall(int Step, string Tool, JsonObject Arguments, bool IsError, JsonObject Result, long Ms);

public sealed class TranscriptFailedException : Exception
{
    public TranscriptFailedException(IReadOnlyList<TranscriptCall> calls, string message)
        : base(message)
    {
        Calls = calls;
    }

    /// <summary>Every call made before the transcript stopped, the failing one last.</summary>
    public IReadOnlyList<TranscriptCall> Calls { get; }
}

/// <summary>
/// A scripted agent transcript (design 371, Testing: the build-a-part scenario): MCP tool calls in order. A step is
/// <c>{"say"?, "tool", "arguments"?, "save"?}</c>. A string argument that is exactly <c>${name}</c> becomes that
/// variable's value, of any JSON type; <c>${name}</c> inside a longer string is replaced by its text. <c>save</c> maps
/// variable names to JSON pointers into the step's result, for the steps after it, as an agent carries a reference
/// from one call to the next. The first error result stops it.
/// </summary>
public sealed class Transcript
{
    static readonly Regex Variable = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    Transcript(string name, IReadOnlyList<JsonObject> steps)
    {
        Name = name;
        Steps = steps;
    }

    public string Name { get; }

    public IReadOnlyList<JsonObject> Steps { get; }

    public static Transcript Load(string path) => Parse(File.ReadAllText(path));

    public static Transcript Parse(string json)
    {
        var root = Json.Parse(json) as JsonObject ?? throw new FormatException("a transcript is a JSON object");
        if (!root.TryGetValue("name", out var n) || n is not string name || name.Length == 0) throw new FormatException("a transcript needs a name");
        if (!root.TryGetValue("steps", out var s) || s is not List<object?> list || list.Count == 0) throw new FormatException("a transcript needs steps");
        var steps = new List<JsonObject>();
        for (int i = 0; i < list.Count; i++)
        {
            var step = list[i] as JsonObject ?? throw new FormatException($"step {i + 1} is not an object");
            if (!step.TryGetValue("tool", out var tool) || tool is not string t || !t.StartsWith("sw_", StringComparison.Ordinal))
                throw new FormatException($"step {i + 1} needs a tool named sw_...");
            if (step.TryGetValue("arguments", out var a) && a is not JsonObject) throw new FormatException($"step {i + 1}: arguments must be an object");
            if (step.TryGetValue("save", out var v) && (v is not JsonObject saves || saves.Any(kv => kv.Value is not string)))
                throw new FormatException($"step {i + 1}: save maps names to JSON pointers");
            steps.Add(step);
        }
        return new Transcript(name, steps);
    }

    /// <summary>Plays every step through <paramref name="call"/>; <paramref name="variables"/> gains what each step saves.</summary>
    public async Task<IReadOnlyList<TranscriptCall>> RunAsync(
        Func<string, JsonObject, Task<(bool IsError, JsonObject Body)>> call,
        IDictionary<string, object?> variables,
        Action<string>? say = null)
    {
        var calls = new List<TranscriptCall>();
        for (int i = 0; i < Steps.Count; i++)
        {
            int number = i + 1;
            var step = Steps[i];
            string tool = (string)step["tool"]!;
            JsonObject arguments;
            try
            {
                arguments = step.TryGetValue("arguments", out var a) ? (JsonObject)Substitute(a, variables)! : new JsonObject();
            }
            catch (KeyNotFoundException e)
            {
                throw new TranscriptFailedException(calls, $"step {number} ({tool}) names {e.Message}, which no earlier step saved");
            }
            if (step.TryGetValue("say", out var line) && line is string text) say?.Invoke($"step {number}: {text}");

            var clock = Stopwatch.StartNew();
            var (isError, body) = await call(tool, arguments).ConfigureAwait(false);
            calls.Add(new TranscriptCall(number, tool, arguments, isError, body, clock.ElapsedMilliseconds));
            if (isError) throw new TranscriptFailedException(calls, $"step {number} ({tool}) answered an error: {Json.Write(body)}");

            if (step.TryGetValue("save", out var save) && save is JsonObject saves)
            {
                foreach (var kv in saves)
                {
                    string pointer = (string)kv.Value!;
                    if (!TryPointer(body, pointer, out var value))
                        throw new TranscriptFailedException(calls, $"step {number} ({tool}): its result has nothing at {pointer}: {Json.Write(body)}");
                    variables[kv.Key] = value;
                }
            }
        }
        return calls;
    }

    /// <summary>The calls as JSON lines, for the run directory.</summary>
    public static void Save(string path, IEnumerable<TranscriptCall> calls) =>
        File.WriteAllLines(path, calls.Select(c => Json.Write(new JsonObject
        {
            ["step"] = (long)c.Step,
            ["tool"] = c.Tool,
            ["arguments"] = c.Arguments,
            ["is_error"] = c.IsError,
            ["result"] = c.Result,
            ["ms"] = c.Ms,
        })));

    static object? Substitute(object? value, IDictionary<string, object?> variables)
    {
        switch (value)
        {
            case string s:
            {
                var whole = Variable.Match(s);
                if (whole.Success && whole.Index == 0 && whole.Length == s.Length) return Lookup(whole.Groups[1].Value, variables);
                return Variable.Replace(s, m => Convert.ToString(Lookup(m.Groups[1].Value, variables), CultureInfo.InvariantCulture) ?? "");
            }
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var kv in o) copy[kv.Key] = Substitute(kv.Value, variables);
                return copy;
            }
            case List<object?> list:
                return list.Select(v => Substitute(v, variables)).ToList();
            default:
                return value;
        }
    }

    static object? Lookup(string name, IDictionary<string, object?> variables) =>
        variables.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException("${" + name + "}");

    /// <summary>RFC 6901: /a/0/b, with ~1 for / and ~0 for ~ in names.</summary>
    static bool TryPointer(object? root, string pointer, out object? value)
    {
        value = root;
        if (pointer.Length == 0) return true;
        if (pointer[0] != '/') return false;
        foreach (var raw in pointer.Substring(1).Split('/'))
        {
            string token = raw.Replace("~1", "/").Replace("~0", "~");
            if (value is JsonObject o && o.TryGetValue(token, out var next))
                value = next;
            else if (value is List<object?> list && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < list.Count)
                value = list[index];
            else
                return false;
        }
        return true;
    }
}
