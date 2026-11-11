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

/// <summary>An empty directory under %TEMP%\swagent-tests, deleted afterwards (best effort).</summary>
sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swagent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test that holds a file open on purpose leaves it; %TEMP% is cleaned in time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
