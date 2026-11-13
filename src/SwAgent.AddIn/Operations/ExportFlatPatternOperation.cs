using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// export.flat_pattern (design 454, Flat-pattern export): a sheet-metal part's flat pattern written as DXF or DWG. A part
/// never saved is refused before SOLIDWORKS is called (CADapter measured a Save As box that waited about 20 minutes).
/// The calls are tried in order until one writes the file (<see cref="FlatPatternExports.Methods"/>), and the file is
/// proven on disk: a DXF's extents against the cut list's flat size and its bend lines as asked, a DWG by its header and
/// size (<see cref="FlatPatternFileCheck"/>). The part keeps its state, path and unsaved-changes flag (checked); when
/// the call marks a clean part as changed (SOLIDWORKS 2026 SP4), its volume, bends and flat size are checked instead.
/// </summary>
public sealed class ExportFlatPatternOperation : Operation<ExportFlatPatternRequest, ExportFlatPatternResult>
{
    readonly AddInServices _services;

    public ExportFlatPatternOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExportFlatPatternResult Run(ExportFlatPatternRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, Name);
        SheetMetalParts.Require(doc, Name);
        var before = doc.Info();
        if (before.Path == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{before.Title} has never been saved, and SOLIDWORKS writes a flat pattern only from a part on disk (otherwise it opens a Save As box that waits for the user); save it with sw_document_save_as first. Nothing was exported",
                new JsonObject { ["field"] = "document", ["document"] = DocumentLocator.Label(before), ["saved"] = false });
        }
        string path = PathRules.Writable(request.Path, "path", request.Overwrite == true, request.Format == FlatPatternFormat.Dxf ? ".dxf" : ".dwg");
        bool bendLines = request.BendLines != false, sketches = request.Sketches == true;

        var sheet = doc.SheetMetal;
        var state = SheetMetalParts.State(doc);
        var cutList = sheet.CutList();
        double unit = sheet.MmPerUnit();
        int bends = sheet.Bends().Count;
        var flat = SheetMetalParts.Flat(cutList, unit, bends);
        if (flat == null && request.Format == FlatPatternFormat.Dxf)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS' cut list for {before.Title} gives no flat size ({SheetMetalParts.LengthProperty} and {SheetMetalParts.WidthProperty}), so a DXF could not be checked against it. Nothing was exported",
                new JsonObject { ["cut_list"] = cutList.Select(p => (object?)$"{p.Key} = {p.Value}").ToList() });
        }
        var expected = new FlatPatternExpectation
        {
            LengthMm = flat?.LengthMm ?? 0,
            WidthMm = flat?.WidthMm ?? 0,
            ToleranceMm = FlatPatternExports.ToleranceMm(cutList, unit),
            MmPerUnit = unit,
            BendLines = bendLines,
            Sketches = sketches,
            Bends = bends,
        };
        var geometry = new Geometry(Volume(doc), bends, flat);

        // The calls work on the active document: the part is made active, and whatever was active is made active again.
        var previous = _services.SolidWorks.ActiveDoc();
        var previousInfo = previous?.Info();
        bool switched = previousInfo == null || !DocumentKeys.Same(DocumentKeys.Of(previousInfo), DocumentKeys.Of(before));
        if (switched) doc.Activate();
        var attempts = new List<string>();
        FlatPatternFile? file = null;
        string method = "";
        try
        {
            foreach (string m in FlatPatternExports.Methods(bendLines, sketches, unsaved: before.Dirty))
            {
                var started = DateTime.UtcNow;
                bool answered = m == FlatPatternExports.FlatPatternView
                    ? sheet.ExportFlatPatternView(path, bendLines)
                    : sheet.ExportToDwg2(path, before.Path, FlatPatternExports.DwgOptions(bendLines, sketches));
                string answer = answered ? "true" : "false";
                if (!WrittenSince(path, started))
                {
                    attempts.Add($"{m} answered {answer} and wrote nothing");
                    continue;
                }
                attempts.Add($"{m} answered {answer} and wrote {new FileInfo(path).Length} bytes");
                method = m;
                file = FlatPatternFileCheck.Check(path, request.Format, started, expected);
                break;
            }
        }
        finally
        {
            if (switched && previous != null) previous.Activate();
        }
        if (file == null)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS wrote no flat pattern to {path}; tried {string.Join("; ", attempts)}",
                new JsonObject { ["path"] = path, ["attempts"] = attempts.Select(a => (object?)a).ToList() });
        }

        var after = doc.Info();
        // The 6.D live run: on SOLIDWORKS 2026 SP4, ExportToDWG2 marks a saved part as having unsaved changes, and the
        // API has no call to clear the flag. That is reported (marked_dirty), never undone by saving the user's file;
        // the path and the state are still verified, a flag cleared (a save) still fails, and since the flag no longer
        // guards the part, its volume, bend count and cut-list flat size are measured again and must be what they were.
        bool markedDirty = !before.Dirty && after.Dirty;
        ExportPartOperation.Unchanged(before, markedDirty ? Clean(after) : after);
        StateUnchanged(after, state, SheetMetalParts.State(doc));
        if (markedDirty) geometry.Unchanged(after, method, Geometry.Of(doc, unit));
        file.Checks.Add(markedDirty
            ? $"{after.Title} kept its path ({after.Path}), its state ({WireNames.ForEnum(state)}) and its geometry ({geometry}); {method} marked it as having unsaved changes (measured on SOLIDWORKS 2026 SP4), and it was not saved to clear the flag"
            : $"{after.Title} kept its path ({after.Path}), its unsaved-changes flag ({(after.Dirty ? "dirty" : "clean")}) and its state ({WireNames.ForEnum(state)})");
        if (switched && previousInfo != null) file.Checks.Add($"{after.Title} was made active for the export and {previousInfo.Title} active again afterwards");
        context.Log.Info($"export.flat_pattern: {DocumentKeys.Of(after)} to {path} by {method} ({file.Bytes} bytes)");
        return new ExportFlatPatternResult
        {
            Document = after,
            Path = path,
            Format = request.Format,
            Bytes = file.Bytes,
            Header = file.Header,
            Method = method,
            Attempts = attempts,
            State = state,
            Flat = flat,
            Extents = file.Extents is DxfBox box
                ? new DrawingExtents
                {
                    MinXMm = box.MinX,
                    MinYMm = box.MinY,
                    MaxXMm = box.MaxX,
                    MaxYMm = box.MaxY,
                    LengthMm = Math.Max(box.Width, box.Height),
                    WidthMm = Math.Min(box.Width, box.Height),
                    From = file.ExtentsFrom ?? "",
                }
                : null,
            BendLines = file.BendLines,
            MarkedDirty = markedDirty,
            Layers = file.Layers.ToList(),
            Checks = file.Checks.ToList(),
        };
    }

    /// <summary>The same document as if its unsaved-changes flag were clear: to compare the rest with what it was.</summary>
    static DocumentInfo Clean(DocumentInfo d) => new DocumentInfo { Title = d.Title, Path = d.Path, Type = d.Type, Dirty = false, Visible = d.Visible };

    static double Volume(ISwDocument doc) => doc.SolidBodyCount() == 0 ? 0 : doc.Mass()?.VolumeMm3 ?? 0;

    /// <summary>
    /// What the part is, to tell that an export which marked it as changed did not change it: its solid volume, its bend
    /// count and its cut-list flat size, each measured as the sheet-metal checks measure them.
    /// </summary>
    internal sealed class Geometry
    {
        /// <summary>The cut list's flat size is written rounded; it is read from the same text both times, so this is exact.</summary>
        const double FlatToleranceMm = 1e-6;

        public Geometry(double volumeMm3, int bends, FlatSize? flat)
        {
            VolumeMm3 = volumeMm3;
            Bends = bends;
            Flat = flat;
        }

        public double VolumeMm3 { get; }
        public int Bends { get; }
        public FlatSize? Flat { get; }

        public static Geometry Of(ISwDocument doc, double mmPerUnit)
        {
            var sheet = doc.SheetMetal;
            int bends = sheet.Bends().Count;
            return new Geometry(Volume(doc), bends, SheetMetalParts.Flat(sheet.CutList(), mmPerUnit, bends));
        }

        static string FlatText(FlatSize? f) => f == null ? "none" : FormattableString.Invariant($"{f.LengthMm:0.####} × {f.WidthMm:0.####} mm");

        public override string ToString() => FormattableString.Invariant($"{VolumeMm3:0.###} mm³, {Bends} bends, flat size {FlatText(Flat)}");

        /// <summary>Fails document_unchanged, naming each measure that moved, unless <paramref name="now"/> is this.</summary>
        public void Unchanged(DocumentInfo part, string method, Geometry now)
        {
            var moved = new List<string>();
            if (Math.Abs(now.VolumeMm3 - VolumeMm3) > FeatureChecks.VolumeTolerance * Math.Max(1, Math.Abs(VolumeMm3)))
                moved.Add(FormattableString.Invariant($"its volume {VolumeMm3:0.###} -> {now.VolumeMm3:0.###} mm³"));
            if (now.Bends != Bends) moved.Add($"its bends {Bends} -> {now.Bends}");
            bool sameFlat = Flat == null ? now.Flat == null
                : now.Flat != null && Math.Abs(now.Flat.LengthMm - Flat.LengthMm) <= FlatToleranceMm && Math.Abs(now.Flat.WidthMm - Flat.WidthMm) <= FlatToleranceMm;
            if (!sameFlat) moved.Add($"its flat size (by the cut list) {FlatText(Flat)} -> {FlatText(now.Flat)}");
            if (moved.Count == 0) return;
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"{method} marked {part.Title} as having unsaved changes and changed it: {string.Join("; ", moved)}. It was not saved; undo the change or close it without saving",
                new JsonObject
                {
                    ["check"] = "document_unchanged",
                    ["expected"] = ToString(),
                    ["measured"] = now.ToString(),
                });
        }
    }

    /// <summary>The file is there, not empty, and was written by the call that began at <paramref name="started"/>.</summary>
    static bool WrittenSince(string path, DateTime started)
    {
        try
        {
            ExportFileCheck.Written(path, started);
            return true;
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.VerificationFailed)
        {
            return false;
        }
    }

    /// <summary>An export leaves the part folded or flattened as it was; nothing is folded or flattened on the agent's behalf.</summary>
    static void StateUnchanged(DocumentInfo part, SheetState before, SheetState after)
    {
        if (before == after) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            $"exporting the flat pattern left {part.Title} {WireNames.ForEnum(after)}; it was {WireNames.ForEnum(before)}" +
            (after == SheetState.Flattened ? "; fold it with sw_sheetmetal_flatten {flat: false}" : "; flatten it again with sw_sheetmetal_flatten {flat: true}"),
            new JsonObject { ["check"] = "state_unchanged", ["expected"] = WireNames.ForEnum(before), ["measured"] = WireNames.ForEnum(after) });
    }

    protected override void Verify(ExportFlatPatternRequest request, ExportFlatPatternResult result, IOperationContext context)
    {
        if (!File.Exists(result.Path))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Path} is gone after export.flat_pattern wrote it",
                new JsonObject { ["check"] = "file_written", ["path"] = result.Path });
        }
        var doc = DocumentLocator.FindPart(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        ExportPartOperation.Unchanged(result.Document, doc.Info());
        StateUnchanged(result.Document, result.State, SheetMetalParts.State(doc));
    }
}
