using System;
using System.Collections.Generic;
using System.IO;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using SwAgent.Mcp.Registration;

namespace SwAgent.Mcp.Install;

public sealed class InstallResult
{
    public string Version { get; init; } = "";

    /// <summary>The version folder installed to.</summary>
    public string Directory { get; init; } = "";

    /// <summary>The installed swmcp.exe: the one to run from now on.</summary>
    public string Swmcp { get; init; } = "";

    public AddinAssembly Addin { get; init; } = null!;

    /// <summary>False when install ran from the version folder itself.</summary>
    public bool Copied { get; init; }

    public int FilesCopied { get; init; }

    public int MarksRemoved { get; init; }
}

/// <summary>A version folder uninstall left, and why.</summary>
public sealed record KeptDirectory(string Directory, string Reason);

public sealed class UninstallResult
{
    public IReadOnlyList<string> RemovedKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Removed { get; init; } = Array.Empty<string>();

    public IReadOnlyList<KeptDirectory> Kept { get; init; } = Array.Empty<KeptDirectory>();
}

/// <summary>
/// swmcp install and uninstall (design 371): per user, HKCU only, no administrator rights. Install copies an unzipped
/// release to its version folder, removes the Mark of the Web there, registers that copy's add-in and writes
/// current.txt. Preference recovery and upgrading in place are phase 7's.
/// </summary>
public sealed class Installer
{
    public const string ExeName = "swmcp.exe";

    readonly InstallLayout _layout;
    readonly AddinRegistrar _registrar;

    public Installer(InstallLayout layout, AddinRegistrar registrar)
    {
        _layout = layout;
        _registrar = registrar;
    }

    public InstallResult Install(string source, string version)
    {
        string from = Path.GetFullPath(source);
        string to = _layout.VersionDirectory(version);
        if (!File.Exists(Path.Combine(from, ExeName)) || !File.Exists(AddinIn(from)))
            throw Invalid(from, $"{from} is not an unzipped solidworks-mcp release: it needs {ExeName} and addin\\{AddInIdentity.AssemblyFileName}");
        bool copy = !InstallLayout.SameDirectory(from, to);
        if (copy && InstallLayout.IsInside(to, from))
            throw Invalid(from, $"the release is unzipped in {from}, which holds the install folder {to}; unzip it somewhere else, such as your Downloads folder, and run swmcp install from there");

        int files = copy ? Copy(from, to) : 0;
        int marks = MarkOfTheWeb.RemoveAll(to);
        var addin = _registrar.Register(AddinIn(to));
        File.WriteAllText(_layout.CurrentFile, version + Environment.NewLine);
        return new InstallResult
        {
            Version = version,
            Directory = to,
            Swmcp = Path.Combine(to, ExeName),
            Addin = addin,
            Copied = copy,
            FilesCopied = files,
            MarksRemoved = marks,
        };
    }

    static int Copy(string from, string to)
    {
        int count = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(file, target, overwrite: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"could not write {target} ({e.Message}); if SOLIDWORKS has this version's add-in loaded, close SOLIDWORKS and run swmcp install again",
                    new JsonObject { ["file"] = target });
            }
            count++;
        }
        return count;
    }

    static string AddinIn(string directory) => Path.Combine(directory, "addin", AddInIdentity.AssemblyFileName);

    static SwAgentException Invalid(string path, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["path"] = path });
}
