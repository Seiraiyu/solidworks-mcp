using System;
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
        var preferences = _services.ExportPreferences
            ?? throw new SwAgentException(ErrorCode.ApiError, "export preferences are not set up in this add-in load, so nothing can be exported");
        // Exporters work on the active document: the part is made active for the export, and whatever was active
        // before is made active again afterwards, whatever the outcome.
        var previous = _services.SolidWorks.ActiveDoc();
        var previousInfo = previous?.Info();
        bool switched = previousInfo == null || !DocumentKeys.Same(DocumentKeys.Of(previousInfo), DocumentKeys.Of(before));
        if (switched) doc.Activate();

        var started = DateTime.UtcNow;
        SaveOutcome outcome;
        try
        {
            using (preferences.Apply(Name, settings))
            {
                outcome = doc.Export(path);
            }
        }
        finally
        {
            if (switched && previous != null) previous.Activate();
        }
        var warnings = SolidWorksCodes.SaveWarnings(outcome.Warnings).ToList();
        if (!outcome.Saved)
        {
            var errors = SolidWorksCodes.SaveErrors(outcome.Errors);
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not export {path}: {string.Join(", ", errors.DefaultIfEmpty("no error reported"))}",
                new JsonObject
                {
                    ["path"] = path,
                    ["errors"] = errors.Select(n => (object?)n).ToList(),
                    ["warnings"] = warnings.Select(n => (object?)n).ToList(),
                });
        }
        var file = ExportFileCheck.Check(path, request.Format, started, ExportSettings.SchemaOf(request), bodies);
        var after = doc.Info();
        Unchanged(before, after);
        file.Checks.Add($"{after.Title} kept its path ({after.Path ?? "never saved"}) and its unsaved-changes flag ({(after.Dirty ? "dirty" : "clean")})");
        if (switched && previousInfo != null) file.Checks.Add($"{after.Title} was made active for the export and {previousInfo.Title} active again afterwards");
        context.Log.Info($"export.part: {DocumentKeys.Of(after)} to {path} ({file.Bytes} bytes; {string.Join(", ", settings)})");
        return new ExportPartResult
        {
            Document = after,
            Path = path,
            Format = request.Format,
            Bytes = file.Bytes,
            Header = file.Header,
            Triangles = file.Triangles,
            StepSchema = file.Schema,
            Preferences = settings.Select(s => s.ToString()).ToList(),
            Warnings = warnings,
            Checks = file.Checks.ToList(),
        };
    }

    /// <summary>The file is still there and the part is still where it was, with the flag it had.</summary>
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
    static void Unchanged(DocumentInfo before, DocumentInfo after)
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
