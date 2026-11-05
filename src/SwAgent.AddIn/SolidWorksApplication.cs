using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>The part of SOLIDWORKS the operations read, so they can be tested without SOLIDWORKS. UI thread only.</summary>
public interface ISolidWorksApplication
{
    string RevisionNumber { get; }

    int ProcessId { get; }

    /// <summary>The active document, or null when none is open.</summary>
    DocumentInfo? ActiveDocument();

    int OpenDocumentCount();

    /// <summary>Every open document, hidden ones (loaded by an assembly) included, with its dirty flag.</summary>
    IReadOnlyList<DocumentInfo> OpenDocuments();
}

/// <summary>The real SOLIDWORKS, through the ISldWorks it handed the add-in in ConnectToSW.</summary>
internal sealed class SolidWorksApplication : ISolidWorksApplication
{
    readonly ISldWorks _sw;

    public SolidWorksApplication(ISldWorks sw)
    {
        _sw = sw;
    }

    public string RevisionNumber => _sw.RevisionNumber();

    public int ProcessId => _sw.GetProcessID();

    public DocumentInfo? ActiveDocument() => _sw.ActiveDoc is IModelDoc2 doc ? Describe(doc) : null;

    public int OpenDocumentCount() => _sw.GetDocumentCount();

    public IReadOnlyList<DocumentInfo> OpenDocuments()
    {
        var list = new List<DocumentInfo>();
        if (!(_sw.GetDocuments() is object[] docs)) return list;
        foreach (var o in docs)
        {
            if (o is IModelDoc2 doc) list.Add(Describe(doc));
        }
        return list;
    }

    internal static DocumentInfo Describe(IModelDoc2 doc)
    {
        string path = doc.GetPathName();
        return new DocumentInfo
        {
            Title = doc.GetTitle(),
            Path = string.IsNullOrEmpty(path) ? null : path,
            Type = MapType(doc.GetType()),
            Dirty = doc.GetSaveFlag(),
        };
    }

    internal static DocumentType MapType(int swType) => swType switch
    {
        (int)swDocumentTypes_e.swDocPART => DocumentType.Part,
        (int)swDocumentTypes_e.swDocASSEMBLY => DocumentType.Assembly,
        (int)swDocumentTypes_e.swDocDRAWING => DocumentType.Drawing,
        _ => throw new SwAgentException(ErrorCode.ApiError,
            $"SOLIDWORKS reported document type {swType}, which is not a part, assembly or drawing"),
    };
}
