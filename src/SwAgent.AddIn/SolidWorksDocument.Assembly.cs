using System;
using System.Collections.Generic;

namespace SwAgent.AddIn;

/// <summary>What an assembly document adds to the seam (design 455).</summary>
internal sealed partial class SolidWorksDocument
{
    /// <summary>GetDependencies2(traverse, search, no read-only info) answers file name and full path in turns; the paths are kept.</summary>
    public IReadOnlyList<string> UsedFiles()
    {
        var paths = new List<string>();
        if (!(Doc.GetDependencies2(true, true, false) is object[] pairs)) return paths;
        for (int i = 1; i < pairs.Length; i += 2)
        {
            if (pairs[i] is string path && path.Length > 0 && !paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                paths.Add(path);
        }
        return paths;
    }
}
