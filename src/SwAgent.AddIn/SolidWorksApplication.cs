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

    /// <summary>
    /// Fault testing only (diag.scratch_part): a new part from the default part template, saved to
    /// <paramref name="path"/> and then marked as having unsaved changes. Left open.
    /// </summary>
    DocumentInfo CreateScratchPart(string path);

    /// <summary>Every open document, hidden ones included, as documents the operations can act on.</summary>
    IReadOnlyList<ISwDocument> Documents();

    /// <summary>The active document, or null when none is open.</summary>
    ISwDocument? ActiveDoc();
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

    public IReadOnlyList<ISwDocument> Documents()
    {
        var list = new List<ISwDocument>();
        if (!(_sw.GetDocuments() is object[] docs)) return list;
        foreach (var o in docs)
        {
            if (o is IModelDoc2 doc) list.Add(new SolidWorksDocument(doc));
        }
        return list;
    }

    public ISwDocument? ActiveDoc() => _sw.ActiveDoc is IModelDoc2 doc ? new SolidWorksDocument(doc) : null;

    public DocumentInfo CreateScratchPart(string path)
    {
        string template = _sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        if (string.IsNullOrEmpty(template) || !System.IO.File.Exists(template))
        {
            throw new SwAgentException(ErrorCode.ApiError,
                $"SOLIDWORKS has no usable default part template ('{template}'); set one in Tools > Options > Default Templates");
        }
        if (!(_sw.NewDocument(template, 0, 0, 0) is IModelDoc2 doc))
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not create a part from the template {template}");
        int errors = 0, warnings = 0;
        bool saved = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
        if (!saved)
        {
            _sw.CloseDoc(doc.GetTitle());
            throw new SwAgentException(ErrorCode.ApiError,
                $"SOLIDWORKS could not save the new part to {path} (errors {errors}, warnings {warnings}); it was closed unsaved");
        }
        doc.SetSaveFlag();
        return Describe(doc);
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
