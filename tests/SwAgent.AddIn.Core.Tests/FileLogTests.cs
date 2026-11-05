using System;
using System.IO;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class FileLogTests
{
    [Fact]
    public void Writes_levels_to_a_daily_file_and_hides_debug_by_default()
    {
        string dir = TempDir.Create();
        using (var log = new FileLog(dir, "addin", debug: false, clock: () => new DateTime(2026, 11, 5, 10, 0, 0)))
        {
            log.Info("hello");
            log.Debug("hidden");
            log.Error("bad", new InvalidOperationException("boom"));
        }
        string text = File.ReadAllText(Path.Combine(dir, "addin-20261105.log"));
        Assert.Contains("2026-11-05 10:00:00.000 INFO  [", text);
        Assert.Contains("hello", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Writes_debug_when_enabled()
    {
        string dir = TempDir.Create();
        using (var log = new FileLog(dir, "addin", debug: true, clock: () => new DateTime(2026, 11, 5)))
        {
            log.Debug("shown");
        }
        Assert.Contains("DEBUG", File.ReadAllText(Path.Combine(dir, "addin-20261105.log")));
    }

    [Fact]
    public void Rolls_over_to_a_new_file_each_day()
    {
        string dir = TempDir.Create();
        var now = new DateTime(2026, 11, 5, 23, 59, 59);
        using (var log = new FileLog(dir, "addin", clock: () => now))
        {
            log.Info("first");
            now = now.AddSeconds(2);
            log.Info("second");
        }
        Assert.Contains("first", File.ReadAllText(Path.Combine(dir, "addin-20261105.log")));
        Assert.Contains("second", File.ReadAllText(Path.Combine(dir, "addin-20261106.log")));
    }

    [Fact]
    public void Deletes_its_own_files_older_than_the_retention()
    {
        string dir = TempDir.Create();
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        string old = Path.Combine(dir, "addin-20261001.log");
        string recent = Path.Combine(dir, "addin-20261101.log");
        string foreign = Path.Combine(dir, "server-20261001.log");
        foreach (var f in new[] { old, recent, foreign }) File.WriteAllText(f, "x");
        File.SetLastWriteTime(old, now.AddDays(-20));
        File.SetLastWriteTime(recent, now.AddDays(-4));
        File.SetLastWriteTime(foreign, now.AddDays(-20));

        using (new FileLog(dir, "addin", retentionDays: 14, clock: () => now)) { }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }
}
