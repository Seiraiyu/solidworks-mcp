using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Which open documents use which (design 455, Assembly documents): what an assembly would unload if it closed, which
/// open documents still need a part, and which used documents have unsaved changes. Read through the seam, UI thread only.
/// </summary>
public static class DocumentUse
{
    /// <summary>The open documents <paramref name="doc"/> uses, nested ones included, in the order SOLIDWORKS lists them.</summary>
    public static IReadOnlyList<ISwDocument> Used(ISolidWorksApplication sw, ISwDocument doc)
    {
        var files = doc.UsedFiles();
        if (files.Count == 0) return Array.Empty<ISwDocument>();
        return sw.Documents().Where(d => !Same(d, doc) && Uses(files, d.Info())).ToList();
    }

    /// <summary>The other open documents that use <paramref name="doc"/>.</summary>
    public static IReadOnlyList<ISwDocument> UsedBy(ISolidWorksApplication sw, ISwDocument doc)
    {
        var info = doc.Info();
        if (info.Path == null) return Array.Empty<ISwDocument>();
        return sw.Documents().Where(d => !Same(d, doc) && Uses(d.UsedFiles(), info)).ToList();
    }

    /// <summary>
    /// The documents SOLIDWORKS unloads with <paramref name="doc"/>: those it uses that have no window of their own and
    /// that no document staying open uses.
    /// </summary>
    public static IReadOnlyList<ISwDocument> WouldUnload(ISolidWorksApplication sw, ISwDocument doc)
    {
        var candidates = Used(sw, doc).Where(d => !d.Info().Visible).ToList();
        if (candidates.Count == 0) return candidates;
        var staying = sw.Documents().Where(d => !Same(d, doc) && !candidates.Any(c => Same(c, d))).ToList();
        var stillUsed = staying.SelectMany(d => d.UsedFiles()).ToList();
        return candidates.Where(c => !Uses(stillUsed, c.Info())).ToList();
    }

    /// <summary>True when the agent owns <paramref name="doc"/>, or an open document the agent owns uses it.</summary>
    public static bool OwnedByAgent(ISolidWorksApplication sw, ISwDocument doc, OwnedDocuments owned)
    {
        if (owned.Contains(DocumentKeys.Of(doc.Info()))) return true;
        return UsedBy(sw, doc).Any(u => owned.Contains(DocumentKeys.Of(u.Info())));
    }

    /// <summary>
    /// The same document, whichever object holds it: SolidWorksApplication.Documents() wraps each document anew on every
    /// call, so objects are never compared by reference (4.A live run, round 2).
    /// </summary>
    public static bool Same(ISwDocument a, ISwDocument b) =>
        ReferenceEquals(a, b) || DocumentKeys.Same(DocumentKeys.Of(a.Info()), DocumentKeys.Of(b.Info()));

    static bool Uses(IEnumerable<string> files, DocumentInfo info) =>
        info.Path != null && files.Any(f => DocumentKeys.Same(Path.GetFullPath(f), Path.GetFullPath(info.Path)));
}
