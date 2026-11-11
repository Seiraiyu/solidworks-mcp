using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The component files SOLIDWORKS loaded from somewhere other than where an assembly says they are (plan 4.B; 4.A live
/// run, case 7). When a component's file is not at its stored path, SOLIDWORKS looks for the same file name in other
/// folders (those it has opened this session, the assembly's own) and silently loads what it finds. Comparing the paths
/// the assembly file stores with the paths loaded after opening it names each such file, so nothing is substituted unseen.
/// </summary>
public static class Substitutions
{
    /// <summary>
    /// Each stored path that was not loaded, paired with the loaded path of the same file name in another folder; a
    /// stored path with no such loaded file is missing, not substituted, and is left out.
    /// </summary>
    public static List<SubstitutedFile> Find(IEnumerable<string> stored, IEnumerable<string> loaded)
    {
        var loadedPaths = loaded.Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath).ToList();
        var found = new List<SubstitutedFile>();
        foreach (string expected in stored.Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath).Distinct(DocumentKeys.Comparer))
        {
            if (loadedPaths.Any(p => DocumentKeys.Same(p, expected))) continue;
            string? instead = loadedPaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), Path.GetFileName(expected), StringComparison.OrdinalIgnoreCase));
            if (instead != null) found.Add(new SubstitutedFile { Expected = expected, Loaded = instead });
        }
        return found;
    }
}
