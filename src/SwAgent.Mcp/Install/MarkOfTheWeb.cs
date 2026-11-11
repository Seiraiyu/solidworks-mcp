using System.IO;

namespace SwAgent.Mcp.Install;

/// <summary>
/// The Mark of the Web: the Zone.Identifier stream Windows puts on a downloaded file and on every file unzipped from
/// one. A .NET Framework add-in loaded from a marked DLL fails to load (design 371), so install removes it from the
/// installed copy. File.Copy carries the stream, so the download's own files are left as they were.
/// </summary>
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    public static bool Has(string file) => File.Exists(file + ":" + StreamName);

    /// <summary>Deletes the stream from every file in <paramref name="directory"/> and below; how many had one.</summary>
    public static int RemoveAll(string directory)
    {
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!Has(file)) continue;
            File.Delete(file + ":" + StreamName);
            removed++;
        }
        return removed;
    }
}
