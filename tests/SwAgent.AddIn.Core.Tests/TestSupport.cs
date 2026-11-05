using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace SwAgent.AddIn.Core.Tests;

sealed class MemoryLog : ILog
{
    public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();

    public bool IsDebugEnabled => true;
    public void Debug(string message) => Lines.Enqueue("DEBUG " + message);
    public void Info(string message) => Lines.Enqueue("INFO " + message);
    public void Warn(string message) => Lines.Enqueue("WARN " + message);
    public void Error(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + (exception == null ? "" : " " + exception));

    public bool Contains(string text) => Lines.Any(l => l.Contains(text));

    /// <summary>Waits until a line containing <paramref name="text"/> has been logged.</summary>
    public void WaitFor(string text, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Contains(text))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"no log line containing '{text}'");
            Thread.Sleep(10);
        }
    }
}

static class TempDir
{
    public static string Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
