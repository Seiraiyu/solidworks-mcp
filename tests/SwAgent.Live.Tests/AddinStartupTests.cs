using System;
using Microsoft.Win32;
using SwAgent.Mcp.Registration;
using Xunit;

namespace SwAgent.Live.Tests;

public sealed class AddinStartupTests : IDisposable
{
    readonly string _rootPath = @"Software\solidworks-mcp-tests\startup-" + Guid.NewGuid().ToString("N");
    readonly RegistryKey _root;

    public AddinStartupTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
    }

    int? Raw()
    {
        using var key = _root.OpenSubKey(AddinRegistrar.StartupPath);
        return key?.GetValue("") as int?;
    }

    [Fact]
    public void Turning_startup_off_remembers_the_value_and_restore_puts_it_back()
    {
        using (var key = _root.CreateSubKey(AddinRegistrar.StartupPath)) key.SetValue("", 1, RegistryValueKind.DWord);
        var startup = new AddinStartup(_root);
        Assert.Equal(1, startup.Read());

        startup.TurnOff();
        Assert.Equal(0, Raw());
        startup.TurnOff();
        Assert.Equal(1, startup.Original);

        startup.Restore();
        Assert.Equal(1, Raw());
        Assert.False(startup.Changed);
    }

    [Fact]
    public void Restore_removes_a_key_that_was_not_there()
    {
        var startup = new AddinStartup(_root);
        Assert.Null(startup.Read());
        startup.TurnOff();
        Assert.Equal(0, Raw());
        startup.Restore();
        Assert.Null(Raw());
        using var key = _root.OpenSubKey(AddinRegistrar.StartupPath);
        Assert.Null(key);
    }

    [Fact]
    public void Restore_without_turning_off_changes_nothing()
    {
        using (var key = _root.CreateSubKey(AddinRegistrar.StartupPath)) key.SetValue("", 1, RegistryValueKind.DWord);
        var startup = new AddinStartup(_root);
        startup.Restore();
        Assert.Equal(1, Raw());
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
    }
}

public class ReadinessTests
{
    [Theory]
    [InlineData(0, false, false, Readiness.StartFromDashboard)]
    [InlineData(2, false, false, Readiness.TooMany)]
    [InlineData(1, false, false, Readiness.Ready)]
    [InlineData(1, true, true, Readiness.Ready)]
    [InlineData(1, true, false, Readiness.CloseFirst)]
    public void Decides_what_SOLIDWORKS_needs_before_a_run(int processes, bool addinLoaded, bool diagnosticsOn, Readiness expected)
    {
        Assert.Equal(expected, ReadinessCheck.Decide(processes, addinLoaded, diagnosticsOn));
    }
}
