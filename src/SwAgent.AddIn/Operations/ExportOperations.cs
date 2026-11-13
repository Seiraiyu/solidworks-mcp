using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// export.part (design 371, Export): a copy of the part written as STEP, Parasolid, STL or 3MF with SaveAs as a copy,
/// the export preferences the request needs set for the call and put back (journalled, so a crash mid-export is undone
/// at the next load), and the file proven on disk by its format's header. The part keeps its path and unsaved changes.
/// </summary>
public sealed class ExportPartOperation : Operation<ExportPartRequest, ExportPartResult>
{
    readonly AddInServices _services;

    public ExportPartOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExportPartResult Run(ExportPartRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, Name);
        string path = PathRules.Writable(request.Path, "path", request.Overwrite == true, ExportSettings.Extensions(request.Format));
        var settings = ExportSettings.For(request.Format, request.Options);
        var before = doc.Info();
        int bodies = doc.SolidBodyCount();
        if (bodies == 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{before.Title} has no solid body to export; make one first (feature.extrude)",
                new JsonObject { ["field"] = "document", ["document"] = DocumentLocator.Label(before) });
        }
        var o = Exporter.Write(_services, doc, Name, path, request.Format, ExportSettings.SchemaOf(request.Options), settings, bodies, context);
        return new ExportPartResult
        {
            Document = o.After,
            Path = path,
            Format = request.Format,
            Bytes = o.File.Bytes,
            Header = o.File.Header,
            Triangles = o.File.Triangles,
            StepSchema = o.File.Schema,
            Preferences = settings.Select(s => s.ToString()).ToList(),
            Warnings = o.Warnings,
            Checks = o.File.Checks.ToList(),
        };
    }

    protected override void Verify(ExportPartRequest request, ExportPartResult result, IOperationContext context)
    {
        if (!File.Exists(result.Path))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Path} is gone after export.part wrote it",
                new JsonObject { ["check"] = "file_written", ["path"] = result.Path });
        }
        var doc = DocumentLocator.FindPart(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        Unchanged(result.Document, doc.Info());
    }

    /// <summary>An export is a copy: the part's path and its unsaved-changes flag are what they were.</summary>
    internal static void Unchanged(DocumentInfo before, DocumentInfo after)
    {
        bool samePath = before.Path == null ? after.Path == null : after.Path != null && DocumentKeys.Same(before.Path, after.Path);
        if (samePath && before.Dirty == after.Dirty) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            samePath
                ? $"exporting changed {after.Title}'s unsaved-changes flag from {before.Dirty} to {after.Dirty}"
                : $"exporting moved {before.Title} from {before.Path ?? "never saved"} to {after.Path ?? "never saved"}; it was meant to write a copy",
            new JsonObject
            {
                ["check"] = "document_unchanged",
                ["expected"] = $"path {before.Path ?? "none"}, dirty {before.Dirty}",
                ["measured"] = $"path {after.Path ?? "none"}, dirty {after.Dirty}",
            });
    }
}
