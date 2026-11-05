using System;
using System.IO;
using Microsoft.Win32;
using SwAgent.Contracts;

namespace SwAgent.Mcp.Tests;

/// <summary>A scratch registry root under HKCU\Software\SwAgentTests, deleted afterwards.</summary>
sealed class ScratchRegistry : IDisposable
{
    readonly string _path = @"Software\SwAgentTests\" + Guid.NewGuid().ToString("N");

    public ScratchRegistry()
    {
        Root = Registry.CurrentUser.CreateSubKey(_path);
    }

    public RegistryKey Root { get; }

    public void Dispose()
    {
        Root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, false);
    }
}

static class Payload
{
    /// <summary>The real add-in DLL, copied beside the tests by build/AddInPayload.targets.</summary>
    public static string AddinPath => Path.Combine(AppContext.BaseDirectory, "addin", AddInIdentity.AssemblyFileName);
}
