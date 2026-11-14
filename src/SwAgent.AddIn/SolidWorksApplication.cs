using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Live testing only (diag.scratch_block): a new part from the default part template holding one block from the
    /// origin, <paramref name="xMm"/> by <paramref name="yMm"/> by <paramref name="zMm"/>, saved to <paramref name="path"/>. Left open.
    /// </summary>
    DocumentInfo CreateScratchBlock(string path, double xMm, double yMm, double zMm);

    /// <summary>Every open document, hidden ones included, as documents the operations can act on.</summary>
    IReadOnlyList<ISwDocument> Documents();

    /// <summary>The active document, or null when none is open.</summary>
    ISwDocument? ActiveDoc();

    /// <summary>The title of the command (PropertyManager) the user has open, or null when none is (ISldWorks.GetRunningCommandInfo; a plain read).</summary>
    string? OpenCommand();

    /// <summary>The user's default part template (Tools > Options > Default Templates), or null when none is set.</summary>
    string? DefaultPartTemplate();

    /// <summary>The user's default assembly template (Tools > Options > Default Templates), or null when none is set.</summary>
    string? DefaultAssemblyTemplate();

    /// <summary>The user's default drawing template (Tools > Options > Default Templates), or null when none is set.</summary>
    string? DefaultDrawingTemplate();

    /// <summary>A new document from <paramref name="template"/>, made active; api_error when SOLIDWORKS makes none.</summary>
    ISwDocument NewDocument(string template);

    /// <summary>Opens <paramref name="path"/> silently as a document of <paramref name="type"/>; an assembly with every component resolved, never lightweight.</summary>
    OpenOutcome OpenDocument(string path, DocumentType type, bool readOnly);

    /// <summary>Closes <paramref name="document"/> without saving it.</summary>
    void Close(ISwDocument document);

    /// <summary>
    /// The full paths the file at <paramref name="path"/> stores for the documents it uses, nested ones included, read
    /// from the file without opening it or searching for them (ISldWorks.GetDocumentDependencies2, no search). Empty for
    /// a file that uses nothing.
    /// </summary>
    IReadOnlyList<string> StoredReferences(string path);

    /// <summary>SOLIDWORKS' own ISldWorks, for api.call's app target only (the escape hatch calls it by late binding).</summary>
    object ApiObject();
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
            if (o is IModelDoc2 doc) list.Add(new SolidWorksDocument(_sw, doc));
        }
        return list;
    }

    public ISwDocument? ActiveDoc() => _sw.ActiveDoc is IModelDoc2 doc ? new SolidWorksDocument(_sw, doc) : null;

    public string? OpenCommand()
    {
        int command;
        string title;
        bool uiActive;
        _sw.GetRunningCommandInfo(out command, out title, out uiActive);
        return string.IsNullOrEmpty(title) ? null : title;
    }

    public string? DefaultPartTemplate()
    {
        string template = _sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        return string.IsNullOrEmpty(template) ? null : template;
    }

    public string? DefaultAssemblyTemplate()
    {
        string template = _sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
        return string.IsNullOrEmpty(template) ? null : template;
    }

    public string? DefaultDrawingTemplate()
    {
        string template = _sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
        return string.IsNullOrEmpty(template) ? null : template;
    }

    public ISwDocument NewDocument(string template) =>
        _sw.NewDocument(template, 0, 0, 0) is IModelDoc2 doc
            ? new SolidWorksDocument(_sw, doc)
            : throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not create a document from the template {template}");

    public OpenOutcome OpenDocument(string path, DocumentType type, bool readOnly)
    {
        int errors = 0, warnings = 0;
        int swType = type switch
        {
            DocumentType.Assembly => (int)swDocumentTypes_e.swDocASSEMBLY,
            DocumentType.Drawing => (int)swDocumentTypes_e.swDocDRAWING,
            _ => (int)swDocumentTypes_e.swDocPART,
        };
        int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (readOnly ? (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0);
        // Design 455, Assembly documents: resolved, whatever the user's lightweight setting, so every component can be referenced.
        if (type == DocumentType.Assembly) options |= (int)swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight;
        var doc = _sw.OpenDoc6(path, swType, options, "", ref errors, ref warnings);
        if (doc != null && !doc.Visible)
        {
            // A component loaded only for an assembly: OpenDoc6 hands it back with no window of its own, and ActivateDoc3
            // gives it one (4.A live run, round 3). Visible is set too, in case activating alone ever leaves it hidden.
            int activateErrors = 0;
            _sw.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            if (!doc.Visible) doc.Visible = true;
        }
        return new OpenOutcome(doc == null ? null : new SolidWorksDocument(_sw, doc), errors, warnings);
    }

    public void Close(ISwDocument document) => _sw.CloseDoc(((SolidWorksDocument)document).Doc.GetTitle());

    public IReadOnlyList<string> StoredReferences(string path)
    {
        var paths = new List<string>();
        // Pairs of file name and full path, in turns (4.A live run: GetDependencies2 answers the same way).
        if (!(_sw.GetDocumentDependencies2(path, true, false, false) is object[] pairs)) return paths;
        for (int i = 1; i < pairs.Length; i += 2)
        {
            if (pairs[i] is string stored && stored.Length > 0 && !paths.Exists(p => string.Equals(p, stored, StringComparison.OrdinalIgnoreCase))) paths.Add(stored);
        }
        return paths;
    }

    public object ApiObject() => _sw;

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

    public DocumentInfo CreateScratchBlock(string path, double xMm, double yMm, double zMm)
    {
        string template = DefaultPartTemplate() ?? "";
        if (!System.IO.File.Exists(template))
        {
            throw new SwAgentException(ErrorCode.ApiError,
                $"SOLIDWORKS has no usable default part template ('{template}'); set one in Tools > Options > Default Templates");
        }
        if (!(_sw.NewDocument(template, 0, 0, 0) is IModelDoc2 doc))
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not create a part from the template {template}");
        var part = new SolidWorksDocument(_sw, doc);
        try
        {
            if (!(part.StandardPlane(0) is IFeature front) || !front.Select2(false, 0))
                throw new SwAgentException(ErrorCode.ApiError, "the new part's first plane could not be selected");
            var sketches = doc.SketchManager;
            sketches.InsertSketch(true);
            bool addToDb = sketches.AddToDB;
            sketches.AddToDB = true;
            try
            {
                if (sketches.CreateCornerRectangle(0, 0, 0, xMm / 1000, yMm / 1000, 0) == null)
                    throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not draw the block's rectangle");
            }
            finally
            {
                sketches.AddToDB = addToDb;
            }
            sketches.InsertSketch(true);
            doc.ClearSelection2(true);
            var sketch = part.TopFeatures().Last(f => f.GetTypeName2() == "ProfileFeature");
            if (!sketch.Select2(false, 0)) throw new SwAgentException(ErrorCode.ApiError, "the block's sketch could not be selected");
            var extrude = doc.FeatureManager.FeatureExtrusion3(true, false, false,
                (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, zMm / 1000, 0,
                false, false, false, false, 0, 0, false, false, false, false, true, true, true,
                (int)swStartConditions_e.swStartSketchPlane, 0, false);
            doc.ClearSelection2(true);
            if (extrude == null) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not extrude the block");
            int errors = 0, warnings = 0;
            if (!doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings))
                throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS could not save the block to {path} (errors {errors}, warnings {warnings})");
            return Describe(doc);
        }
        catch
        {
            _sw.CloseDoc(doc.GetTitle());
            throw;
        }
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
            Visible = doc.Visible,
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
