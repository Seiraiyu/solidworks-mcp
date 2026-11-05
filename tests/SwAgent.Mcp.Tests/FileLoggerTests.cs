using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SwAgent.Mcp.Logging;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class FileLoggerTests
{
    static string NewDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Writes_to_the_days_file_with_level_and_category()
    {
        string dir = NewDirectory();
        var now = new DateTime(2026, 11, 5, 9, 30, 0);
        using (var provider = new FileLoggerProvider(dir, "server", clock: () => now))
        {
            provider.CreateLogger("SwAgent.Mcp").LogWarning("sw_status {Outcome}", "timeout");
        }
        string line = Assert.Single(File.ReadAllLines(Path.Combine(dir, "server-20261105.log")));
        Assert.Equal("2026-11-05 09:30:00.000 WARN  SwAgent.Mcp: sw_status timeout", line);
    }

    [Fact]
    public void Deletes_its_own_files_older_than_the_retention_only()
    {
        string dir = NewDirectory();
        foreach (var name in new[] { "server-20261020.log", "server-20261022.log", "server-20261105.log", "addin-20261001.log" })
            File.WriteAllText(Path.Combine(dir, name), "x");
        using (new FileLoggerProvider(dir, "server", retentionDays: 14, clock: () => new DateTime(2026, 11, 5)))
        {
        }
        Assert.Equal(new[] { "addin-20261001.log", "server-20261022.log", "server-20261105.log" },
            Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal));
    }
}
