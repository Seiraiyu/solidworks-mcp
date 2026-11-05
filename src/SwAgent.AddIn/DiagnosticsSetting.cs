using Microsoft.Win32;

namespace SwAgent.AddIn;

/// <summary>Whether diag.* operations may run: HKCU\Software\solidworks-mcp, value Diagnostics = 1, read once at load.</summary>
public static class DiagnosticsSetting
{
    public const string KeyPath = @"Software\solidworks-mcp";
    public const string ValueName = "Diagnostics";

    public static bool Enabled(RegistryKey root)
    {
        using var key = root.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is int value && value == 1;
    }
}
