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
        var type = request.Type ?? DocumentType.Part;
        if (type == DocumentType.Drawing)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                "document.new makes parts and assemblies in this version; drawings come in a later release",
                new JsonObject { ["field"] = "type" });
        }
        var kind = Kind.Of(type);
        string? path = request.Path == null ? null : PathRules.Writable(request.Path, "path", overwrite: false, kind.Extension);
        string template;
        if (request.Template != null)
        {
            template = PathRules.Existing(request.Template, "template", kind.TemplateExtension);
        }
        else
        {
            template = (type == DocumentType.Assembly ? _services.SolidWorks.DefaultAssemblyTemplate() : _services.SolidWorks.DefaultPartTemplate())
                ?? throw new SwAgentException(ErrorCode.OperationFailed,
                    $"SOLIDWORKS has no default {kind.Noun} template; set one in Tools > Options > Default Templates, or pass template");
            if (!File.Exists(template))
            {
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"the default {kind.Noun} template {template} does not exist; fix it in Tools > Options > Default Templates, or pass template",
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
                    $"the new {kind.Noun} could not be saved to {path}, so it was closed: {string.Join(", ", SolidWorksCodes.SaveErrors(saved.Errors).DefaultIfEmpty("no error reported"))}",
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

    /// <summary>
    /// The document exists, is active, is of the type asked, has the template's three planes and origin, and is on disk
    /// when a path was given; a new assembly has no command left open (Begin Assembly: design 455, Session preferences).
    /// </summary>
    protected override void Verify(DocumentNewRequest request, DocumentNewResult result, IOperationContext context)
    {
        var type = request.Type ?? DocumentType.Part;
        var kind = Kind.Of(type);
        var active = _services.SolidWorks.ActiveDoc();
        var info = active?.Info();
        if (info == null || !DocumentKeys.Same(DocumentKeys.Of(info), DocumentKeys.Of(result.Document)))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the new {kind.Noun} {result.Document.Title} is not the active document");
        if (info.Type != type)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the template {result.Template} made {info.Type}, not {kind.Article}");
        if (active!.StandardPlane(2) == null || active.Origin() == null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the new {kind.Noun} has no three default planes and origin; is {result.Template} {kind.Article} template?");
        if (request.Path != null && !File.Exists(request.Path))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported the {kind.Noun} saved to {request.Path}, but the file is not there");
        if (type == DocumentType.Assembly && _services.SolidWorks.OpenCommand() is string command)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS left '{command}' open on the new assembly {info.Title}: the automation preference insert_component_for_new_assembly did not take (addin.state lists it under preferences_not_applied). Cancel it in SOLIDWORKS before the next change",
                new JsonObject { ["command"] = command, ["document"] = DocumentLocator.Label(info) });
        }
    }

    /// <summary>The words and extensions that differ between a new part and a new assembly.</summary>
    sealed class Kind
    {
        static readonly Kind Part = new Kind("part", "a part", ".SLDPRT", ".PRTDOT");
        static readonly Kind Assembly = new Kind("assembly", "an assembly", ".SLDASM", ".ASMDOT");

        Kind(string noun, string article, string extension, string templateExtension)
        {
            Noun = noun;
            Article = article;
            Extension = extension;
            TemplateExtension = templateExtension;
        }

        public string Noun { get; }
        public string Article { get; }
        public string Extension { get; }
        public string TemplateExtension { get; }

        public static Kind Of(DocumentType type) => type == DocumentType.Assembly ? Assembly : Part;
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
        // A document loaded only because an assembly uses it has no window: opening it gives it one (design 455).
        if (open != null && open.Info().Visible)
        {
            open.Activate();
            return new DocumentOpenResult { Document = open.Info(), AlreadyOpen = true, ReadOnly = open.ReadOnly };
        }

        // Read before opening: once open, SOLIDWORKS holds the paths it found instead (plan 4.B; 4.A live run, case 7).
        var stored = PathRules.TypeOf(path) == DocumentType.Assembly ? _services.SolidWorks.StoredReferences(path) : new string[0];
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
        // Design 455: opened silently, so SOLIDWORKS never asked where a component's file went: the files it uses that are not there.
        var used = outcome.Document.UsedFiles();
        var missing = used.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0) context.Log.Warn($"document.open: {path} uses {missing.Count} file(s) that are not there: {string.Join(", ", missing)}");
        var substituted = Substitutions.Find(stored, used);
        foreach (var s in substituted) context.Log.Warn($"document.open: {path} names {s.Expected}, which is not there; SOLIDWORKS loaded {s.Loaded} instead");
        return new DocumentOpenResult { Document = info, ReadOnly = outcome.Document.ReadOnly, Warnings = warnings, Missing = missing, Substituted = substituted };
    }

    /// <summary>The file is open as the document reported, read-only when asked.</summary>
    protected override void Verify(DocumentOpenRequest request, DocumentOpenResult result, IOperationContext context)
    {
        string path = Path.GetFullPath(request.Path);
        var doc = _services.SolidWorks.Documents().FirstOrDefault(d => d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), path));
        if (doc == null) throw new SwAgentException(ErrorCode.VerificationFailed, $"{path} is not open after document.open");
        if (!doc.Info().Visible) throw new SwAgentException(ErrorCode.VerificationFailed, $"{path} is loaded but has no window after document.open");
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
        var referenced = info.Type == DocumentType.Assembly ? SaveUsed(doc, info, context) : new System.Collections.Generic.List<SavedFile>();
        var result = Saving.Save(doc, path, doc.Save, context);
        result.Referenced = referenced;
        return result;
    }

    /// <summary>
    /// Design 455, owner's answer 454: every document the assembly uses that has unsaved changes is saved first, where it
    /// is, and checked on disk. A failure stops before the assembly is saved and says which were saved already.
    /// </summary>
    System.Collections.Generic.List<SavedFile> SaveUsed(ISwDocument assembly, DocumentInfo info, IOperationContext context)
    {
        var dirty = DocumentUse.Used(_services.SolidWorks, assembly).Where(d => d.Info().Dirty).ToList();
        var saved = new System.Collections.Generic.List<SavedFile>();
        foreach (var used in dirty)
        {
            string path = Path.GetFullPath(used.Info().Path!);
            try
            {
                var one = Saving.Save(used, path, used.Save, context);
                saved.Add(new SavedFile { Document = one.Document, Path = one.Path, Bytes = one.Bytes });
            }
            catch (SwAgentException e)
            {
                var details = e.Error.Details ?? new JsonObject();
                details["saved"] = saved.Select(s => (object?)s.Path).ToList();
                details["assembly"] = DocumentLocator.Label(info);
                throw new SwAgentException(e.Code,
                    $"{e.Message}. {info.Title} was not saved{(saved.Count == 0 ? "" : $"; {saved.Count} of the documents it uses were saved before this one (details.saved)")}",
                    details);
            }
        }
        return saved;
    }

    protected override void Verify(DocumentSaveRequest request, DocumentSaveResult result, IOperationContext context)
    {
        foreach (var used in result.Referenced) Saving.Verify(_services.SolidWorks, used.Path);
        Saving.Verify(_services.SolidWorks, result.Path);
    }
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
        var sw = _services.SolidWorks;
        var doc = DocumentLocator.Find(sw, request.Document);
        var info = doc.Info();
        string key = DocumentKeys.Of(info);
        var usedBy = DocumentUse.UsedBy(sw, doc).Select(d => DocumentLocator.Label(d.Info())).ToList();
        if (!info.Visible)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Title} has no window: it is loaded only because {(usedBy.Count == 0 ? "an open assembly" : string.Join(", ", usedBy))} uses it. Close the assembly instead",
                new JsonObject { ["field"] = "document", ["document"] = key, ["used_by"] = usedBy.Select(u => (object?)u).ToList() });
        }
        // Design 455: a document an assembly the agent owns uses is the agent's too.
        bool owned = DocumentUse.OwnedByAgent(sw, doc, _services.Runtime.Owned);
        if (!owned && request.UsersDocument != true)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Title} was not opened or created by the agent, so it is the user's and was not closed; pass users_document: true only if the user asked for it to be closed",
                new JsonObject { ["field"] = "users_document", ["document"] = key });
        }
        // A document still used stays loaded with its changes; otherwise it and everything only it used go.
        var unloaded = usedBy.Count == 0 ? DocumentUse.WouldUnload(sw, doc).Select(d => d.Info()).ToList() : new System.Collections.Generic.List<DocumentInfo>();
        var lost = (usedBy.Count == 0 && info.Dirty ? new[] { info } : new DocumentInfo[0]).Concat(unloaded.Where(u => u.Dirty)).ToList();
        if (lost.Count > 0 && request.Discard != true)
        {
            string what = lost.Count == 1 && ReferenceEquals(lost[0], info)
                ? $"{info.Title} has unsaved changes"
                : $"closing {info.Title} would lose the unsaved changes of {string.Join(", ", lost.Select(l => l.Title))}";
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{what} and it was not closed; save {(info.Type == DocumentType.Assembly ? "the assembly (document.save saves its components too)" : "it")} first, or pass discard: true to lose them",
                new JsonObject { ["field"] = "discard", ["document"] = key, ["unsaved"] = lost.Select(l => (object?)DocumentLocator.Label(l)).ToList() });
        }
        sw.Close(doc);
        // SOLIDWORKS keeps a used part's changes in memory but clears its save flag with the window: set it again, so the
        // assembly's save writes the part and closing the assembly asks for discard (4.A live run, round 4).
        if (usedBy.Count > 0 && info.Dirty) doc.MarkDirty();
        _services.Runtime.Owned.Remove(key);
        foreach (var gone in unloaded) _services.Runtime.Owned.Remove(DocumentKeys.Of(gone));
        context.Log.Info($"document.close: {key}{(lost.Count > 0 ? $", unsaved changes discarded ({string.Join(", ", lost.Select(l => l.Title))})" : "")}"
            + $"{(unloaded.Count > 0 ? $", unloaded {string.Join(", ", unloaded.Select(u => u.Title))}" : "")}{(usedBy.Count > 0 ? $", still used by {string.Join(", ", usedBy)}" : "")}{(owned ? "" : ", the user's document, as asked")}");
        return new DocumentCloseResult { Closed = info, Discarded = lost.Count > 0, Unloaded = unloaded, StillUsedBy = usedBy };
    }

    /// <summary>
    /// The closed document is no longer open, nor anything it unloaded; or, when an assembly still uses it, it is still
    /// loaded but has no window.
    /// </summary>
    protected override void Verify(DocumentCloseRequest request, DocumentCloseResult result, IOperationContext context)
    {
        var open = _services.SolidWorks.Documents().Select(d => d.Info()).ToList();
        string key = DocumentKeys.Of(result.Closed);
        var still = open.FirstOrDefault(d => DocumentKeys.Same(DocumentKeys.Of(d), key));
        if (result.StillUsedBy.Count > 0)
        {
            if (still != null && still.Visible)
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{key} still has its window after closing it");
            if (still != null && result.Closed.Dirty && !still.Dirty)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"{result.Closed.Title} lost its window, and SOLIDWORKS keeps its changes in memory but reports it saved, so nothing would save or protect them; " +
                    $"save it from the assembly (document.save of {string.Join(", ", result.StillUsedBy)}) now",
                    new JsonObject { ["document"] = key, ["dirty"] = false });
            }
            if (still == null)
            {
                // SOLIDWORKS unloaded it although an open document was read as using it (4.A live run, case 6).
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"{result.Closed.Title} was unloaded although {string.Join(", ", result.StillUsedBy)} was read as using it" +
                    (result.Closed.Dirty ? "; its unsaved changes are lost" : "; it had no unsaved changes"),
                    new JsonObject { ["document"] = key, ["unloaded"] = true, ["dirty"] = result.Closed.Dirty });
            }
            return;
        }
        if (still != null) throw new SwAgentException(ErrorCode.VerificationFailed, $"{key} is still open after closing it");
        var left = result.Unloaded.Where(u => open.Any(d => DocumentKeys.Same(DocumentKeys.Of(d), DocumentKeys.Of(u)))).Select(u => u.Title).ToList();
        if (left.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"{result.Closed.Title} closed, but SOLIDWORKS kept {string.Join(", ", left)} loaded, which nothing open uses",
                new JsonObject { ["loaded"] = left.Select(l => (object?)l).ToList() });
        }
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
