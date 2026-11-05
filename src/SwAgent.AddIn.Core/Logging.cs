using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SwAgent.AddIn.Core;

public interface ILog
{
    bool IsDebugEnabled { get; }
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// A dependency-free daily log file, written by its own background thread so a call on SOLIDWORKS' UI thread
/// never waits for the disk. A log that cannot be written is dropped, never thrown.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    readonly string _directory;
    readonly string _prefix;
    readonly Func<DateTime> _clock;
    readonly BlockingCollection<Entry> _queue = new BlockingCollection<Entry>(new ConcurrentQueue<Entry>(), 100_000);
    readonly Thread _writer;

    public FileLog(string directory, string prefix, bool debug = false, int retentionDays = 14, Func<DateTime>? clock = null)
    {
        _directory = directory;
        _prefix = prefix;
        _clock = clock ?? (() => DateTime.Now);
        IsDebugEnabled = debug;
        Directory.CreateDirectory(directory);
        Prune(retentionDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "swagent-log" };
        _writer.Start();
    }

    /// <summary><c>%LOCALAPPDATA%\solidworks-mcp\logs</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "logs");

    public bool IsDebugEnabled { get; }

    public void Debug(string message)
    {
        if (IsDebugEnabled) Write("DEBUG", message);
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : message + Environment.NewLine + exception);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    void Write(string level, string message)
    {
        var now = _clock();
        string stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = $"{stamp} {level,-5} [{Thread.CurrentThread.ManagedThreadId}] {message}";
        try
        {
            _queue.TryAdd(new Entry(now, line));
        }
        catch (InvalidOperationException)
        {
            // Logged after Dispose: dropped.
        }
    }

    void WriteLoop()
    {
        StreamWriter? writer = null;
        DateTime day = default;
        try
        {
            foreach (var entry in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (writer == null || entry.Time.Date != day)
                    {
                        writer?.Dispose();
                        day = entry.Time.Date;
                        string path = Path.Combine(_directory, _prefix + "-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                        writer = new StreamWriter(
                            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                            new UTF8Encoding(false));
                    }
                    writer.WriteLine(entry.Line);
                    if (_queue.Count == 0) writer.Flush();
                }
                catch (Exception)
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    void Prune(int retentionDays)
    {
        var cutoff = _clock().AddDays(-retentionDays);
        foreach (var file in Directory.GetFiles(_directory, _prefix + "-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception)
            {
                // Another process may hold it; try again next start.
            }
        }
    }

    readonly struct Entry
    {
        public Entry(DateTime time, string line)
        {
            Time = time;
            Line = line;
        }

        public DateTime Time { get; }
        public string Line { get; }
    }
}

/// <summary>A log that discards everything.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new NullLog();
    public bool IsDebugEnabled => false;
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
