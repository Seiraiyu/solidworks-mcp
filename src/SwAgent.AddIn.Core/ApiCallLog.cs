using System;
using System.Globalization;
using System.IO;
using System.Text;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>One escape-hatch call as it is logged.</summary>
public sealed class ApiCallRecord
{
    public string Target { get; set; } = "";
    public string? Document { get; set; }
    public string Member { get; set; } = "";

    /// <summary>The arguments as the request gave them (JSON).</summary>
    public object? Args { get; set; }

    /// <summary>The result as returned to the agent (JSON), or null when it failed.</summary>
    public object? Result { get; set; }

    /// <summary>The failure, or null when it succeeded.</summary>
    public SwAgentError? Error { get; set; }

    public long DurationMs { get; set; }
}

/// <summary>
/// <c>api-calls.log</c> beside the add-in log (design 371, Escape hatch): every api.call in full -- target, member,
/// arguments, result or error, duration -- one JSON object per line, so its use shows which operations to add. Kept to
/// 10 MB: past that the file becomes <c>api-calls.1.log</c>, replacing the one before. Never throws.
/// </summary>
public sealed class ApiCallLog
{
    public const long MaxBytes = 10L * 1024 * 1024;

    readonly string _path;
    readonly Func<DateTime> _clock;
    readonly object _gate = new object();

    public ApiCallLog(string path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary><c>%LOCALAPPDATA%\solidworks-mcp\logs\api-calls.log</c>.</summary>
    public static string DefaultPath => Path.Combine(FileLog.DefaultDirectory, "api-calls.log");

    public string FilePath => _path;

    public void Write(ApiCallRecord record)
    {
        try
        {
            var line = new JsonObject
            {
                ["time"] = _clock().ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                ["target"] = record.Target,
            };
            if (record.Document != null) line["document"] = record.Document;
            line["member"] = record.Member;
            line["args"] = record.Args;
            line["outcome"] = record.Error == null ? "ok" : WireNames.ForEnum(record.Error.Code);
            if (record.Error == null) line["result"] = record.Result;
            else line["error"] = JsonCodec.ToJson(record.Error);
            line["duration_ms"] = record.DurationMs;
            string text = Json.Write(line) + Environment.NewLine;
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var file = new FileInfo(_path);
                if (file.Exists && file.Length + text.Length > MaxBytes)
                {
                    string old = Path.ChangeExtension(_path, ".1.log");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
                File.AppendAllText(_path, text, new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // A log that cannot be written is dropped, never thrown into the call.
        }
    }
}
