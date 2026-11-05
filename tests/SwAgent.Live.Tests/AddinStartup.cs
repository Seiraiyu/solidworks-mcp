using Microsoft.Win32;
using SwAgent.Mcp.Registration;

namespace SwAgent.Live.Tests;

/// <summary>
/// The add-in's "load at start up" switch (HKCU\Software\SolidWorks\AddInsStartup\{clsid}), which swmcp addin register
/// sets to 1. With it on, SOLIDWORKS loads the add-in as it starts, before a live run can turn Diagnostics on, and the
/// suites cannot run in that load (3.A live run). A run turns it off before it asks for SOLIDWORKS to be started, so
/// the add-in loads on the run's first call with Diagnostics on, and puts it back as it was when the run ends.
/// Turned off only while SOLIDWORKS is not running (SOLIDWORKS may write the switch as it exits); put back when the run ends.
/// </summary>
public sealed class AddinStartup
{
    readonly RegistryKey _root;
    bool _remembered;

    public AddinStartup(RegistryKey root)
    {
        _root = root;
    }

    /// <summary>The value before this run changed it (null when the key was absent); meaningful once <see cref="Changed"/>.</summary>
    public int? Original { get; private set; }

    /// <summary>True while the run has the switch off and has not restored it.</summary>
    public bool Changed { get; private set; }

    /// <summary>The switch's value now, or null when its key is absent.</summary>
    public int? Read()
    {
        using var key = _root.OpenSubKey(AddinRegistrar.StartupPath);
        return key?.GetValue("") is int value ? value : (int?)null;
    }

    /// <summary>Sets the switch to 0, remembering the value it had the first time.</summary>
    public void TurnOff()
    {
        if (!_remembered)
        {
            Original = Read();
            _remembered = true;
        }
        using (var key = _root.CreateSubKey(AddinRegistrar.StartupPath)) key.SetValue("", 0, RegistryValueKind.DWord);
        Changed = true;
    }

    /// <summary>Puts the switch back as it was before <see cref="TurnOff"/>, removing the key if it was absent. Does nothing if it was never turned off.</summary>
    public void Restore()
    {
        if (!Changed) return;
        if (Original is int value)
        {
            using var key = _root.CreateSubKey(AddinRegistrar.StartupPath);
            key.SetValue("", value, RegistryValueKind.DWord);
        }
        else
        {
            _root.DeleteSubKey(AddinRegistrar.StartupPath, throwOnMissingSubKey: false);
        }
        Changed = false;
    }
}

/// <summary>What SOLIDWORKS needs before a live run can use it.</summary>
public enum Readiness
{
    /// <summary>One SOLIDWORKS, and the add-in either not loaded yet (the run's first call loads it, with Diagnostics on) or loaded with diagnostics on.</summary>
    Ready,

    /// <summary>No SOLIDWORKS: it must be started from the 3DEXPERIENCE tab.</summary>
    StartFromDashboard,

    /// <summary>The add-in was loaded without diagnostics: SOLIDWORKS must be closed (by the owner) and started again.</summary>
    CloseFirst,

    /// <summary>More than one SOLIDWORKS: the run cannot tell which is meant.</summary>
    TooMany,
}

public static class ReadinessCheck
{
    public static Readiness Decide(int processes, bool addinLoaded, bool diagnosticsOn)
    {
        if (processes == 0) return Readiness.StartFromDashboard;
        if (processes > 1) return Readiness.TooMany;
        return !addinLoaded || diagnosticsOn ? Readiness.Ready : Readiness.CloseFirst;
    }
}
