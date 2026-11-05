using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace SwAgent.Mcp.Logging;

/// <summary>
/// Appends log lines to <c>&lt;prefix&gt;-&lt;yyyyMMdd&gt;.log</c> in one directory, and on start deletes that prefix's
/// files older than the retention. Small and dependency-free, like the add-in's own log.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    readonly string _directory;
    readonly string _prefix;
    readonly Func<DateTime> _clock;
    readonly object _lock = new object();

    public FileLoggerProvider(string directory, string prefix, int retentionDays = 14, Func<DateTime>? clock = null)
    {
        _directory = directory;
        _prefix = prefix;
        _clock = clock ?? (() => DateTime.Now);
        Directory.CreateDirectory(directory);
        DeleteOlderThan(_clock().Date.AddDays(-retentionDays));
    }

    /// <summary>Where the server logs: <c>SWMCP_LOG_DIRECTORY</c> when set (the tests use it), else beside the add-in's log.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("SWMCP_LOG_DIRECTORY") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _clock();
        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {Name(level),-5} {category}: {message}";
        if (exception != null) line += Environment.NewLine + exception;
        string path = Path.Combine(_directory, $"{_prefix}-{now:yyyyMMdd}.log");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the server down.
            }
        }
    }

    void DeleteOlderThan(DateTime cutoff)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
        {
            string stamp = Path.GetFileNameWithoutExtension(file).Substring(_prefix.Length + 1);
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use; the next start deletes it.
                }
            }
        }
    }

    static string Name(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => level.ToString(),
    };

    sealed class FileLogger : ILogger
    {
        readonly FileLoggerProvider _provider;
        readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
