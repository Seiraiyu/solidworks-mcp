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
