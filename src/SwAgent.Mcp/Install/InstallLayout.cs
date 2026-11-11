using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SwAgent.Mcp.Install;

/// <summary>
/// Where swmcp installs (design 371, Installer and release): %LOCALAPPDATA%\solidworks-mcp\app\&lt;version&gt;\, one
/// folder per version so a SOLIDWORKS still holding an earlier add-in's DLLs does not block a new install, and
/// app\current.txt naming the version installed last.
/// </summary>
public sealed class InstallLayout
{
    public InstallLayout(string localAppData)
    {
        Root = Path.Combine(localAppData, "solidworks-mcp", "app");
    }

    public string Root { get; }

    public string CurrentFile => Path.Combine(Root, "current.txt");

    public string VersionDirectory(string version) => Path.Combine(Root, version);

    /// <summary>Every folder under the root, in name order (current.txt is a file, so it is not one).</summary>
    public IReadOnlyList<string> InstalledVersionDirectories() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    /// <summary>The install folder for a build's informational version: the version without its +commit.</summary>
    public static string FolderName(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+');
        string version = plus < 0 ? informationalVersion : informationalVersion.Substring(0, plus);
        if (version.Length == 0 || version == "." || version == ".." || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{informationalVersion}' does not name an install folder", nameof(informationalVersion));
        return version;
    }

    public static bool SameDirectory(string a, string b) => string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="directory"/> is <paramref name="parent"/> or below it.</summary>
    public static bool IsInside(string directory, string parent) =>
        (Normal(directory) + Path.DirectorySeparatorChar).StartsWith(Normal(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static string Normal(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
