using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.AddIn;

/// <summary>What an assembly document adds to the seam (design 455).</summary>
internal sealed partial class SolidWorksDocument
{
    /// <summary>
    /// For an assembly, the files of its components in the active configuration, walked from the root component: every
    /// component SOLIDWORKS has not suppressed (it holds their documents in memory), and every suppressed one whose file
    /// is missing (so document.open can list it). A suppressed component's file is not used: SOLIDWORKS does not keep it
    /// loaded, and closing that document's window unloads it (4.A live run, case 6). For anything else,
    /// GetDependencies2(traverse, search, no read-only info), which answers file name and full path in turns.
    /// </summary>
    public IReadOnlyList<string> UsedFiles()
    {
        var paths = new List<string>();
        if (Doc is IAssemblyDoc)
        {
            if (Doc.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) is IComponent2 root) LoadedFiles(root, paths);
            return paths;
        }
        if (!(Doc.GetDependencies2(true, true, false) is object[] pairs)) return paths;
        for (int i = 1; i < pairs.Length; i += 2)
        {
            if (pairs[i] is string path) AddPath(paths, path);
        }
        return paths;
    }

    static void LoadedFiles(IComponent2 parent, List<string> into)
    {
        if (!(parent.GetChildren() is object[] children)) return;
        foreach (var o in children)
        {
            if (!(o is IComponent2 child)) continue;
            string path = child.GetPathName() ?? "";
            if (child.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
            {
                if (path.Length > 0 && !File.Exists(path)) AddPath(into, path);
                continue;
            }
            AddPath(into, path);
            LoadedFiles(child, into);
        }
    }

    static void AddPath(List<string> paths, string path)
    {
        if (path.Length > 0 && !paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) paths.Add(path);
    }

    public ISwAssembly? Assembly() => Doc is IAssemblyDoc ? new SolidWorksAssembly(_sw, Doc) : null;
}
