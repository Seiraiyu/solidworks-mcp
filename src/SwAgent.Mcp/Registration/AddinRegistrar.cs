using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using SwAgent.Contracts;

namespace SwAgent.Mcp.Registration;

public sealed class RegistrationStatus
{
    public bool ClsidKey { get; set; }
    public bool ProgIdKey { get; set; }
    public bool AddInsKey { get; set; }
    public bool StartupKey { get; set; }

    /// <summary>The DLL the CLSID points at, or null.</summary>
    public string? Path { get; set; }

    public bool FileExists { get; set; }

    public bool Registered => ClsidKey && ProgIdKey && AddInsKey && StartupKey && FileExists;
}

/// <summary>
/// Registers the add-in for the current user only: COM under Software\Classes and SOLIDWORKS' add-in list under
/// Software\SolidWorks, both relative to <c>root</c> (HKCU in use, a scratch key in tests). No regasm, no HKLM.
/// </summary>
public sealed class AddinRegistrar
{
    /// <summary>The .NET "managed category" COM writes for every managed class.</summary>
    public const string ManagedCategory = "{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}";
    public const string RuntimeVersion = "v4.0.30319";

    public static readonly string ClsidPath = @"Software\Classes\CLSID\" + AddInIdentity.ClsidKey;
    public static readonly string ProgIdPath = @"Software\Classes\" + AddInIdentity.ProgId;
    public static readonly string AddInsPath = @"Software\SolidWorks\AddIns\" + AddInIdentity.ClsidKey;
    public static readonly string StartupPath = @"Software\SolidWorks\AddInsStartup\" + AddInIdentity.ClsidKey;

    readonly RegistryKey _root;

    public AddinRegistrar(RegistryKey root)
    {
        _root = root;
    }

    /// <summary>Writes every key; idempotent. Throws invalid_argument if <paramref name="path"/> is not the add-in.</summary>
    public AddinAssembly Register(string path)
    {
        var assembly = AddinAssembly.Read(path);
        string codeBase = new Uri(assembly.Path).AbsoluteUri;

        using (var clsid = _root.CreateSubKey(ClsidPath))
        {
            clsid.SetValue("", AddInIdentity.ProgId);
            using (var server = clsid.CreateSubKey("InprocServer32"))
            {
                server.SetValue("", "mscoree.dll");
                server.SetValue("ThreadingModel", "Both");
                WriteServer(server, assembly, codeBase);
                foreach (var stale in server.GetSubKeyNames())
                {
                    if (stale != assembly.Version) server.DeleteSubKeyTree(stale);
                }
                using (var version = server.CreateSubKey(assembly.Version)) WriteServer(version, assembly, codeBase);
            }
            using (var progId = clsid.CreateSubKey("ProgId")) progId.SetValue("", AddInIdentity.ProgId);
            clsid.CreateSubKey(@"Implemented Categories\" + ManagedCategory).Dispose();
        }

        using (var progId = _root.CreateSubKey(ProgIdPath))
        {
            progId.SetValue("", AddInIdentity.ClassName);
            using (var clsid = progId.CreateSubKey("CLSID")) clsid.SetValue("", AddInIdentity.ClsidKey);
        }

        using (var addIns = _root.CreateSubKey(AddInsPath))
        {
            addIns.SetValue("", 1, RegistryValueKind.DWord);
            addIns.SetValue("Title", AddInIdentity.Title);
            addIns.SetValue("Description", AddInIdentity.Description);
        }

        using (var startup = _root.CreateSubKey(StartupPath)) startup.SetValue("", 1, RegistryValueKind.DWord);
        return assembly;
    }

    /// <summary>Removes exactly the keys <see cref="Register"/> writes; returns those that existed.</summary>
    public IReadOnlyList<string> Unregister()
    {
        var removed = new List<string>();
        foreach (var path in new[] { ClsidPath, ProgIdPath, AddInsPath, StartupPath })
        {
            using (var key = _root.OpenSubKey(path))
            {
                if (key == null) continue;
            }
            _root.DeleteSubKeyTree(path, false);
            removed.Add(path);
        }
        return removed;
    }

    public RegistrationStatus Status()
    {
        var status = new RegistrationStatus
        {
            ClsidKey = Exists(ClsidPath),
            ProgIdKey = Exists(ProgIdPath),
            AddInsKey = Exists(AddInsPath),
            StartupKey = Exists(StartupPath),
        };
        using (var server = _root.OpenSubKey(ClsidPath + @"\InprocServer32"))
        {
            if (server?.GetValue("CodeBase") is string codeBase && Uri.TryCreate(codeBase, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                status.Path = uri.LocalPath;
                status.FileExists = File.Exists(uri.LocalPath);
            }
        }
        return status;
    }

    bool Exists(string path)
    {
        using var key = _root.OpenSubKey(path);
        return key != null;
    }

    static void WriteServer(RegistryKey key, AddinAssembly assembly, string codeBase)
    {
        key.SetValue("Class", AddInIdentity.ClassName);
        key.SetValue("Assembly", assembly.FullName);
        key.SetValue("RuntimeVersion", RuntimeVersion);
        key.SetValue("CodeBase", codeBase);
    }
}
