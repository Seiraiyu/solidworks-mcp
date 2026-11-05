using System;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class DocumentNewOperation : Operation<DocumentNewRequest, DocumentNewResult>
{
    readonly AddInServices _services;

    public DocumentNewOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentNewResult Run(DocumentNewRequest request, IOperationContext context)
    {
        if (request.Type != null && request.Type != DocumentType.Part)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                "document.new makes parts only in this version; assemblies and drawings come in later releases",
                new JsonObject { ["field"] = "type" });
        }
        string? path = request.Path == null ? null : PathRules.Writable(request.Path, "path", overwrite: false, ".SLDPRT");
        string template;
        if (request.Template != null)
        {
            template = PathRules.Existing(request.Template, "template", ".PRTDOT");
        }
        else
        {
            template = _services.SolidWorks.DefaultPartTemplate()
                ?? throw new SwAgentException(ErrorCode.OperationFailed,
                    "SOLIDWORKS has no default part template; set one in Tools > Options > Default Templates, or pass template");
            if (!File.Exists(template))
            {
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"the default part template {template} does not exist; fix it in Tools > Options > Default Templates, or pass template",
                    new JsonObject { ["template"] = template });
            }
        }

        var doc = _services.SolidWorks.NewDocument(template);
        if (path != null)
        {
            var saved = doc.SaveAs(path);
            if (!saved.Saved || !File.Exists(path))
            {
                string title = doc.Info().Title;
                _services.SolidWorks.Close(doc);
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"the new part could not be saved to {path}, so it was closed: {string.Join(", ", SolidWorksCodes.SaveErrors(saved.Errors).DefaultIfEmpty("no error reported"))}",
                    new JsonObject
                    {
                        ["path"] = path,
                        ["errors"] = SolidWorksCodes.SaveErrors(saved.Errors).Select(n => (object?)n).ToList(),
                        ["rolled_back"] = new System.Collections.Generic.List<object?> { title },
                    });
            }
        }
        var info = doc.Info();
        _services.Runtime.DocumentOpened(DocumentKeys.Of(info), owned: true);
        context.Log.Info($"document.new: {DocumentKeys.Of(info)} from {template}");
        return new DocumentNewResult { Document = info, Template = template };
    }

    /// <summary>The part exists, is active, has the template's three planes and origin, and is on disk when a path was given.</summary>
    protected override void Verify(DocumentNewRequest request, DocumentNewResult result, IOperationContext context)
    {
        var active = _services.SolidWorks.ActiveDoc();
        var info = active?.Info();
        if (info == null || !DocumentKeys.Same(DocumentKeys.Of(info), DocumentKeys.Of(result.Document)))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the new part {result.Document.Title} is not the active document");
        if (info.Type != DocumentType.Part)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the template {result.Template} made {info.Type}, not a part");
        if (active!.StandardPlane(2) == null || active.Origin() == null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the new part has no three default planes and origin; is {result.Template} a part template?");
        if (request.Path != null && !File.Exists(request.Path))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported the part saved to {request.Path}, but the file is not there");
    }
}

public sealed class DocumentOpenOperation : Operation<DocumentOpenRequest, DocumentOpenResult>
{
    readonly AddInServices _services;

    public DocumentOpenOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentOpenResult Run(DocumentOpenRequest request, IOperationContext context)
    {
        string path = PathRules.Existing(request.Path, "path", ".SLDPRT", ".SLDASM", ".SLDDRW");
        bool readOnly = request.ReadOnly == true;
        var open = _services.SolidWorks.Documents().FirstOrDefault(d => d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), path));
        if (open != null)
        {
            open.Activate();
            return new DocumentOpenResult { Document = open.Info(), AlreadyOpen = true, ReadOnly = open.ReadOnly };
        }

        var outcome = _services.SolidWorks.OpenDocument(path, PathRules.TypeOf(path), readOnly);
        var warnings = SolidWorksCodes.LoadWarnings(outcome.Warnings).ToList();
        if (outcome.Document == null)
        {
            var errors = SolidWorksCodes.LoadErrors(outcome.Errors);
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not open {path}: {string.Join(", ", errors.DefaultIfEmpty("no error reported"))}",
                new JsonObject
                {
                    ["path"] = path,
                    ["errors"] = errors.Select(n => (object?)n).ToList(),
                    ["warnings"] = warnings.Select(n => (object?)n).ToList(),
                });
        }
        var info = outcome.Document.Info();
        _services.Runtime.DocumentOpened(DocumentKeys.Of(info), owned: true);
        return new DocumentOpenResult { Document = info, ReadOnly = outcome.Document.ReadOnly, Warnings = warnings };
    }

    /// <summary>The file is open as the document reported, read-only when asked.</summary>
    protected override void Verify(DocumentOpenRequest request, DocumentOpenResult result, IOperationContext context)
    {
        string path = Path.GetFullPath(request.Path);
        var doc = _services.SolidWorks.Documents().FirstOrDefault(d => d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), path));
        if (doc == null) throw new SwAgentException(ErrorCode.VerificationFailed, $"{path} is not open after document.open");
        if (request.ReadOnly == true && !result.AlreadyOpen && !doc.ReadOnly)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{path} was asked to open read-only but is writable");
    }
}

/// <summary>What document.save and document.save_as share: the save itself, then proof on disk that it happened.</summary>
static class Saving
{
    /// <summary>File times are not exact; a write within this much before the call began still counts as this call's.</summary>
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    public static DocumentSaveResult Save(ISwDocument doc, string path, Func<SaveOutcome> save, IOperationContext context)
    {
        if (doc.ReadOnly)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"{doc.Info().Title} is open read-only and cannot be saved; close it and open it again without read_only",
                new JsonObject { ["errors"] = new System.Collections.Generic.List<object?> { "swReadOnlySaveError" } });
        }
        var started = DateTime.UtcNow;
        var outcome = save();
        var warnings = SolidWorksCodes.SaveWarnings(outcome.Warnings).ToList();
        if (!outcome.Saved)
        {
            var errors = SolidWorksCodes.SaveErrors(outcome.Errors);
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not save {path}: {string.Join(", ", errors.DefaultIfEmpty("no error reported"))}",
                new JsonObject
                {
                    ["path"] = path,
                    ["errors"] = errors.Select(n => (object?)n).ToList(),
                    ["warnings"] = warnings.Select(n => (object?)n).ToList(),
                });
        }
        var file = new FileInfo(path);
        if (!file.Exists || file.LastWriteTimeUtc < started - Slack)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS reported {path} saved, but {(file.Exists ? $"the file was last written at {file.LastWriteTimeUtc:o}, before this call" : "the file is not there")}",
                new JsonObject { ["path"] = path });
        }
        context.Log.Info($"saved {path} ({file.Length} bytes)");
        return new DocumentSaveResult { Document = doc.Info(), Path = path, Bytes = file.Length, Warnings = warnings };
    }

    /// <summary>The document is open at <paramref name="path"/> with no unsaved changes.</summary>
    public static void Verify(ISolidWorksApplication sw, string path)
    {
        var doc = sw.Documents().FirstOrDefault(d => d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), path));
        if (doc == null) throw new SwAgentException(ErrorCode.VerificationFailed, $"no open document is {path} after saving it");
        if (doc.Info().Dirty) throw new SwAgentException(ErrorCode.VerificationFailed, $"{path} still has unsaved changes after saving it");
    }
}

public sealed class DocumentSaveOperation : Operation<DocumentSaveRequest, DocumentSaveResult>
{
    readonly AddInServices _services;

    public DocumentSaveOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentSaveResult Run(DocumentSaveRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, request.Document);
        var info = doc.Info();
        if (info.Path == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Title} has never been saved, so it has nowhere to be saved to; use document.save_as with a path",
                new JsonObject { ["field"] = "document", ["document"] = info.Title });
        }
        string path = Path.GetFullPath(info.Path);
        return Saving.Save(doc, path, doc.Save, context);
    }

    protected override void Verify(DocumentSaveRequest request, DocumentSaveResult result, IOperationContext context) =>
        Saving.Verify(_services.SolidWorks, result.Path);
}

public sealed class DocumentSaveAsOperation : Operation<DocumentSaveAsRequest, DocumentSaveResult>
{
    readonly AddInServices _services;

    public DocumentSaveAsOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentSaveResult Run(DocumentSaveAsRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, request.Document);
        var info = doc.Info();
        string extension = info.Type switch
        {
            DocumentType.Assembly => ".SLDASM",
            DocumentType.Drawing => ".SLDDRW",
            _ => ".SLDPRT",
        };
        string path = PathRules.Writable(request.Path, "path", request.Overwrite == true, extension);
        string oldKey = DocumentKeys.Of(info);
        var other = _services.SolidWorks.Documents().FirstOrDefault(d =>
            d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), path) && !DocumentKeys.Same(DocumentKeys.Of(d.Info()), oldKey));
        if (other != null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{path} is open as another document ({other.Info().Title}); close it first or choose another path",
                new JsonObject { ["field"] = "path", ["path"] = path });
        }
        var result = Saving.Save(doc, path, () => doc.SaveAs(path), context);
        if (!DocumentKeys.Same(DocumentKeys.Of(result.Document), path))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS wrote {path} but the document is still {DocumentKeys.Of(result.Document)}",
                new JsonObject { ["path"] = path });
        }
        _services.Runtime.DocumentRenamed(oldKey, path);
        return result;
    }

    protected override void Verify(DocumentSaveAsRequest request, DocumentSaveResult result, IOperationContext context) =>
        Saving.Verify(_services.SolidWorks, result.Path);
}

public sealed class DocumentCloseOperation : Operation<DocumentCloseRequest, DocumentCloseResult>
{
    readonly AddInServices _services;

    public DocumentCloseOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentCloseResult Run(DocumentCloseRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, request.Document);
        var info = doc.Info();
        string key = DocumentKeys.Of(info);
        bool owned = _services.Runtime.Owned.Contains(key);
        if (!owned && request.UsersDocument != true)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Title} was not opened or created by the agent, so it is the user's and was not closed; pass users_document: true only if the user asked for it to be closed",
                new JsonObject { ["field"] = "users_document", ["document"] = key });
        }
        if (info.Dirty && request.Discard != true)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Title} has unsaved changes and was not closed; save it first, or pass discard: true to lose them",
                new JsonObject { ["field"] = "discard", ["document"] = key });
        }
        _services.SolidWorks.Close(doc);
        _services.Runtime.Owned.Remove(key);
        context.Log.Info($"document.close: {key}{(info.Dirty ? ", unsaved changes discarded" : "")}{(owned ? "" : ", the user's document, as asked")}");
        return new DocumentCloseResult { Closed = info, Discarded = info.Dirty };
    }

    /// <summary>No open document is the one closed.</summary>
    protected override void Verify(DocumentCloseRequest request, DocumentCloseResult result, IOperationContext context)
    {
        string key = DocumentKeys.Of(result.Closed);
        if (_services.SolidWorks.Documents().Any(d => DocumentKeys.Same(DocumentKeys.Of(d.Info()), key)))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{key} is still open after closing it");
    }
}

/// <summary>What document.rebuild and model.rebuild_status share.</summary>
static class RebuildStatus
{
    public static RebuildResult Of(ISwDocument doc)
    {
        var items = doc.WhatsWrong().ToList();
        return new RebuildResult
        {
            Document = doc.Info(),
            Errors = items.Count(i => !i.Warning),
            Warnings = items.Count(i => i.Warning),
            Items = items,
        };
    }

    public static void Verify(ISolidWorksApplication sw, RebuildResult result)
    {
        var doc = DocumentLocator.Find(sw, DocumentKeys.Of(result.Document));
        var now = Of(doc);
        if (now.Errors != result.Errors || now.Warnings != result.Warnings)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the rebuild status changed while it was read ({result.Errors} errors, {result.Warnings} warnings, now {now.Errors} and {now.Warnings}); ask again");
        }
    }
}

public sealed class DocumentRebuildOperation : Operation<DocumentRebuildRequest, RebuildResult>
{
    readonly AddInServices _services;

    public DocumentRebuildOperation(AddInServices services)
    {
        _services = services;
    }

    protected override RebuildResult Run(DocumentRebuildRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, request.Document);
        bool rebuilt = doc.Rebuild(request.Force == true);
        var result = RebuildStatus.Of(doc);
        if (!rebuilt && result.Items.Count == 0)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS reported that {result.Document.Title} did not rebuild, but lists no feature with an error");
        }
        return result;
    }

    protected override void Verify(DocumentRebuildRequest request, RebuildResult result, IOperationContext context) =>
        RebuildStatus.Verify(_services.SolidWorks, result);
}

public sealed class ModelRebuildStatusOperation : Operation<ModelRebuildStatusRequest, RebuildResult>
{
    readonly AddInServices _services;

    public ModelRebuildStatusOperation(AddInServices services)
    {
        _services = services;
    }

    protected override RebuildResult Run(ModelRebuildStatusRequest request, IOperationContext context) =>
        RebuildStatus.Of(DocumentLocator.Find(_services.SolidWorks, request.Document));

    protected override void Verify(ModelRebuildStatusRequest request, RebuildResult result, IOperationContext context) =>
        RebuildStatus.Verify(_services.SolidWorks, result);
}
