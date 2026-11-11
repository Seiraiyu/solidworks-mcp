using System;
using System.IO;
using Microsoft.Win32;
using SwAgent.Contracts;
using SwAgent.Mcp.Install;

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

/// <summary>
/// An unzipped release as a user meets it: swmcp.exe (a stand-in: install only needs it present), a runtime file, and
/// the real add-in DLL in addin\ (registration reads its metadata), each marked as downloaded.
/// </summary>
sealed class FakeRelease : IDisposable
{
    readonly ScratchDirectory? _owned;

    /// <summary>A release in a new scratch directory, or in <paramref name="at"/>, which the caller cleans up.</summary>
    public FakeRelease(string? at = null)
    {
        if (at == null)
        {
            _owned = new ScratchDirectory();
            at = _owned.Path;
        }
        Path = at;
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "addin"));
        File.WriteAllText(System.IO.Path.Combine(Path, "swmcp.exe"), "a stand-in for swmcp.exe");
        File.WriteAllText(System.IO.Path.Combine(Path, "System.Runtime.dll"), "a stand-in for the runtime");
        File.Copy(Payload.AddinPath, System.IO.Path.Combine(Path, "addin", AddInIdentity.AssemblyFileName), overwrite: true);
        foreach (var file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
            File.WriteAllText(file + ":" + MarkOfTheWeb.StreamName, "[ZoneTransfer]\r\nZoneId=3\r\n");
    }

    public string Path { get; }

    /// <summary>The number of files in it: swmcp.exe, the runtime stand-in, the add-in.</summary>
    public const int Files = 3;

    public void Dispose() => _owned?.Dispose();
}
