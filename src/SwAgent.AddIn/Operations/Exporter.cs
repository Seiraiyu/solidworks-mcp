using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What an export wrote: the file as read back, the preferences set for it, SOLIDWORKS' warnings, and the document after.</summary>
public sealed class ExportOutcome
{
    public ExportOutcome(ExportFile file, List<string> warnings, DocumentInfo after)
    {
        File = file;
        Warnings = warnings;
        After = after;
    }

    public ExportFile File { get; }

    public List<string> Warnings { get; }

    public DocumentInfo After { get; }
}

/// <summary>
/// The exporter export.part and export.assembly share (design 371 and 455, Export): a copy written with SaveAs as a copy
/// (or, for STEP AP242, published), the export preferences set for the call and put back (journalled, so a crash
/// mid-export is undone at the next load), the document made active for the export and the active one put back, the file
/// proven on disk by its format's header, and the document's path and unsaved-changes flag unchanged.
/// </summary>
public static class Exporter
{
    public static ExportOutcome Write(AddInServices services, ISwDocument doc, string operation, string path, ExportFormat format, StepSchema schema,
        IReadOnlyList<PreferenceSetting> settings, int bodies, IOperationContext context)
    {
        var before = doc.Info();
        var preferences = services.ExportPreferences
            ?? throw new SwAgentException(ErrorCode.ApiError, "export preferences are not set up in this add-in load, so nothing can be exported");
        // Exporters work on the active document: the document is made active for the export, and whatever was active
        // before is made active again afterwards, whatever the outcome.
        var previous = services.SolidWorks.ActiveDoc();
        var previousInfo = previous?.Info();
        bool switched = previousInfo == null || !DocumentKeys.Same(DocumentKeys.Of(previousInfo), DocumentKeys.Of(before));
        if (switched) doc.Activate();

        // AP242 is published, not saved as (3.E live run: SaveAs3 with swStepAP = 242 writes AP203; the owner chose
        // PublishSTEP242File). swStepAP is still set to 242 for the call, and put back.
        bool publish = format == ExportFormat.Step && schema == StepSchema.Ap242;
        var started = DateTime.UtcNow;
        SaveOutcome outcome;
        IReadOnlyList<string> readBack;
        IReadOnlyList<string> publishAttempts = Array.Empty<string>();
        try
        {
            using (preferences.Apply(operation, settings))
            {
                // What SOLIDWORKS holds just before it writes, for a file it did not make from them (3.E live run).
                readBack = preferences.ReadBack(settings);
                if (publish)
                {
                    publishAttempts = PublishAp242(doc, path);
                    outcome = new SaveOutcome(true, 0, 0);
                }
                else
                {
                    outcome = doc.Export(path);
                }
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
        ExportFile file;
        try
        {
            file = ExportFileCheck.Check(path, format, started, schema, bodies);
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.VerificationFailed && readBack.Count > 0)
        {
            var details = e.Error.Details ?? new JsonObject();
            details["read_back"] = readBack.Select(r => (object?)r).ToList();
            throw new SwAgentException(e.Code, $"{e.Message}; the export preferences SOLIDWORKS read back just before the export: {string.Join(", ", readBack)}", details);
        }
        if (publish) file.Checks.Insert(0, $"published as AP242 (SaveAs3 does not write it): {string.Join("; ", publishAttempts)}");
        var after = doc.Info();
        ExportPartOperation.Unchanged(before, after);
        file.Checks.Add($"{after.Title} kept its path ({after.Path ?? "never saved"}) and its unsaved-changes flag ({(after.Dirty ? "dirty" : "clean")})");
        if (switched && previousInfo != null) file.Checks.Add($"{after.Title} was made active for the export and {previousInfo.Title} active again afterwards");
        context.Log.Info($"{operation}: {DocumentKeys.Of(after)} to {path} ({file.Bytes} bytes; {string.Join(", ", settings)})");
        return new ExportOutcome(file, warnings, after);
    }

    /// <summary>
    /// STEP AP242 by the chain the 3.E live run called for, stopping at the first attempt that writes: PublishSTEP242File
    /// with the path; the same with its extension in upper case (round 4 answered InvalidPath); GetMBDSTEP242Data with
    /// PublishSTEP242File2. Every attempt and SOLIDWORKS' answer is returned, and when none writes they all go into
    /// operation_failed, so the next run reads what SOLIDWORKS said rather than guessing.
    /// </summary>
    static IReadOnlyList<string> PublishAp242(ISwDocument doc, string path)
    {
        var attempts = new List<string>();
        var errors = new List<object?>();
        bool Tried(string what, int code)
        {
            string answer = SolidWorksCodes.Step242Error(code);
            attempts.Add($"{what}: {answer}");
            errors.Add(answer);
            return code == 0;
        }

        if (Tried($"PublishSTEP242File({path})", doc.PublishStep242(path))) return attempts;
        string extension = Path.GetExtension(path);
        string upper = path.Substring(0, path.Length - extension.Length) + extension.ToUpperInvariant();
        if (!string.Equals(upper, path, StringComparison.Ordinal) && Tried($"PublishSTEP242File({upper})", doc.PublishStep242(upper))) return attempts;
        if (Tried("GetMBDSTEP242Data + PublishSTEP242File2", doc.PublishStep242WithData(path))) return attempts;

        bool licence = errors.Contains(SolidWorksCodes.Step242Error(4));
        throw new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS did not publish {path} as STEP AP242; tried {string.Join("; ", attempts)}" +
            (licence ? " (SOLIDWORKS reports no MBD licence for STEP 242)" : "") +
            ". AP242 is written only by SOLIDWORKS MBD's publisher; ap214 needs none",
            new JsonObject
            {
                ["path"] = path,
                ["attempts"] = attempts.Select(a => (object?)a).ToList(),
                ["errors"] = errors,
                ["warnings"] = new List<object?>(),
            });
    }
}
