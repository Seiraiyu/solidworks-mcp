using System;
using System.IO;

namespace SwAgent.Contracts;

/// <summary>Where the live suite keeps the documents it creates; diag.scratch_part refuses any other place.</summary>
public static class LiveScratch
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "live-scratch");

    /// <summary>True for a .SLDPRT path inside <see cref="Root"/>, after resolving any "..".</summary>
    public static bool IsScratchPart(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && full.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase);
    }
}
