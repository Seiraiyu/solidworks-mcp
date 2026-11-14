using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// export.drawing (design 611, Export): a drawing's sheets written as one PDF (IExportPdfData naming the sheets) or as one
/// DXF or DWG per sheet (each sheet shown in turn and written with SaveAs as a copy, the sheet that was shown put back),
/// the export preferences set for the call and put back (journalled, through <see cref="Exporter.Prepared{T}"/>), and every
/// file proven on disk (<see cref="DrawingFileCheck"/>). A drawing never saved is refused before SOLIDWORKS is called. The
/// drawing keeps its path, sheets, views, annotations and tables (<see cref="DrawingSnapshot"/>, before and after), and no
/// model it shows is marked as changed; a drawing the export marks as changed is reported (marked_dirty, 6.D's finding)
/// and never saved behind the user.
/// </summary>
public sealed class ExportDrawingOperation : Operation<ExportDrawingRequest, ExportDrawingResult>
{
    readonly AddInServices _services;

    public ExportDrawingOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExportDrawingResult Run(ExportDrawingRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var before = doc.Info();
        if (before.Path == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{before.Title} has never been saved, and SOLIDWORKS exports a drawing only from a file on disk (otherwise it opens a Save As box that waits for the user); save it with sw_document_save_as first. Nothing was exported",
                new JsonObject { ["field"] = "document", ["document"] = DocumentLocator.Label(before), ["saved"] = false });
        }
        DrawingChangeGuard.Require(_services.SolidWorks, doc, drawing, Name);
        var sheets = Sheets(doc, drawing, request);
        var files = Files(request, sheets);
        var expected = sheets.Select(s => Expect(drawing, s)).ToList();
        var settings = DrawingExportSettings.For(request.Format);
        var snapshot = DrawingSnapshot.Take(drawing);
        var models = ModelStates.Take(_services.SolidWorks, doc);
        string? shown = drawing.ActiveSheet();

        var warnings = new List<string>();
        var (written, madeActive) = Exporter.Prepared(_services, doc, Name, settings, () =>
            request.Format == DrawingExportFormat.Pdf
                ? new List<DrawingFile> { Pdf(drawing, files[0], sheets, expected, warnings) }
                : PerSheet(doc, drawing, request.Format, files, sheets, expected, shown, warnings));

        var after = doc.Info();
        bool markedDirty = !before.Dirty && after.Dirty;
        ExportPartOperation.Unchanged(before, markedDirty ? Clean(after) : after);
        var checks = written.SelectMany(f => f.Checks).ToList();
        snapshot.Unchanged(DrawingSnapshot.Take(drawing), before.Title);
        if (shown != null && !string.Equals(drawing.ActiveSheet(), shown, StringComparison.Ordinal))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{shown} was the sheet shown before the export, and {drawing.ActiveSheet() ?? "no sheet"} is shown after it; show it again with sw_sheet_activate",
                new JsonObject { ["check"] = "sheet_shown", ["expected"] = shown, ["measured"] = drawing.ActiveSheet() });
        }
        var changed = models.Changed(_services.SolidWorks, doc);
        if (changed.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"exporting the drawing touched a model it shows: {string.Join("; ", changed)}",
                new JsonObject { ["check"] = "models", ["models"] = changed.Select(c => (object?)c).ToList() });
        }
        checks.Add(markedDirty
            ? $"{after.Title} kept its path ({after.Path}), its {snapshot}; SOLIDWORKS marked it as having unsaved changes (an export may on SOLIDWORKS 2026 SP4), and it was not saved to clear the flag"
            : $"{after.Title} kept its path ({after.Path}), its unsaved-changes flag ({(after.Dirty ? "dirty" : "clean")}) and its {snapshot}");
        checks.Add($"the models it shows kept their unsaved-changes flags and rebuild errors; {shown ?? "no sheet"} is shown, as before");
        if (madeActive != null) checks.Add($"{after.Title} was made active for the export and {madeActive} active again afterwards");
        context.Log.Info($"export.drawing: {DocumentKeys.Of(after)} {string.Join(", ", sheets)} to {string.Join(", ", files)} ({string.Join(", ", settings)})");

        return new ExportDrawingResult
        {
            Document = after,
            Format = request.Format,
            Sheets = sheets.ToList(),
            Files = written.Select((f, i) => Describe(f, files[i], request.Format == DrawingExportFormat.Pdf ? sheets : new List<string> { sheets[i] })).ToList(),
            MarkedDirty = markedDirty,
            Preferences = settings.Select(s => s.ToString()).ToList(),
            Warnings = warnings.Distinct().ToList(),
            Checks = checks,
        };
    }

    /// <summary>The sheets asked for, by reference or name, in the drawing's order; every sheet when none are named.</summary>
    List<string> Sheets(ISwDocument doc, ISwDrawing drawing, ExportDrawingRequest request)
    {
        var all = drawing.SheetNames();
        if (request.Sheets == null) return all.ToList();
        if (request.Sheets.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "/sheets: name at least one sheet, or leave sheets out to export them all", new JsonObject { ["field"] = "/sheets" });
        var named = new List<string>();
        for (int i = 0; i < request.Sheets.Count; i++)
        {
            string name = _services.References.ResolveSheet(doc, drawing, request.Sheets[i], $"/sheets/{i}");
            if (named.Contains(name, StringComparer.Ordinal))
                throw new SwAgentException(ErrorCode.InvalidArgument, $"/sheets/{i}: {name} is named twice", new JsonObject { ["field"] = $"/sheets/{i}" });
            named.Add(name);
        }
        return all.Where(s => named.Contains(s, StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// The file each sheet goes to: one PDF at the path; one DXF or DWG at the path for one sheet, and for several,
    /// &lt;folder&gt;\&lt;name&gt;-&lt;sheet&gt;&lt;extension&gt; beside it. Each must be writable (overwrite to replace).
    /// </summary>
    static List<string> Files(ExportDrawingRequest request, IReadOnlyList<string> sheets)
    {
        string extension = DrawingExportSettings.Extension(request.Format);
        string path = PathRules.Writable(request.Path, "path", request.Overwrite == true, extension);
        if (request.Format == DrawingExportFormat.Pdf || sheets.Count == 1) return new List<string> { path };
        string folder = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path);
        var files = sheets.Select(s => PathRules.Writable(Path.Combine(folder, $"{stem}-{SheetFileName(s)}{Path.GetExtension(path)}"), "path", request.Overwrite == true, extension)).ToList();
        var twice = files.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice != null)
            throw new SwAgentException(ErrorCode.InvalidArgument, $"path: two sheets would be written to {twice.Key}; rename one (sw_sheet_set) or export them one at a time",
                new JsonObject { ["field"] = "path" });
        return files;
    }

    /// <summary>A sheet's name as it goes into a file name: any character a file name cannot hold made _.</summary>
    internal static string SheetFileName(string sheet)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(sheet.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>A sheet's size and each view's drawn extents and scale, read before the export.</summary>
    static SheetExpectation Expect(ISwDrawing drawing, string sheet)
    {
        var facts = drawing.ReadSheet(sheet) ?? throw new SwAgentException(ErrorCode.ReferenceUnresolved, $"the sheet {sheet} is not in the drawing", new JsonObject { ["sheet"] = sheet });
        var expectation = new SheetExpectation { Name = sheet, WidthMm = facts.WidthMm, HeightMm = facts.HeightMm };
        foreach (var name in drawing.ViewNames(sheet))
        {
            if (!(drawing.ReadView(name) is ViewFacts view) || !(DrawingReader.Extents(drawing, view) is SheetBox extents)) continue;
            expectation.Views.Add(new ViewExpectation { Name = name, Extents = extents, Scale = DrawingReader.Ratio(view.Scale) });
        }
        return expectation;
    }

    static DrawingFile Pdf(ISwDrawing drawing, string path, List<string> sheets, List<SheetExpectation> expected, List<string> warnings)
    {
        var started = DateTime.UtcNow;
        var outcome = drawing.ExportPdf(path, sheets);
        Saved(outcome, path, warnings);
        return DrawingFileCheck.Pdf(path, started, expected);
    }

    /// <summary>Each sheet shown in turn and written to its own file; the sheet that was shown is shown again, however it ends.</summary>
    static List<DrawingFile> PerSheet(ISwDocument doc, ISwDrawing drawing, DrawingExportFormat format, List<string> files, List<string> sheets, List<SheetExpectation> expected, string? shown, List<string> warnings)
    {
        var written = new List<DrawingFile>();
        try
        {
            for (int i = 0; i < sheets.Count; i++)
            {
                if (!string.Equals(drawing.ActiveSheet(), sheets[i], StringComparison.Ordinal) && !drawing.ActivateSheet(sheets[i]))
                {
                    throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not show the sheet {sheets[i]}, so it was not exported{Done(written, files)}",
                        new JsonObject { ["sheet"] = sheets[i], ["written"] = files.Take(written.Count).Select(f => (object?)f).ToList() });
                }
                var started = DateTime.UtcNow;
                Saved(doc.Export(files[i]), files[i], warnings);
                written.Add(format == DrawingExportFormat.Dxf ? DrawingFileCheck.Dxf(files[i], started, expected[i]) : DrawingFileCheck.Dwg(files[i], started));
            }
        }
        finally
        {
            if (shown != null && !string.Equals(drawing.ActiveSheet(), shown, StringComparison.Ordinal)) drawing.ActivateSheet(shown);
        }
        return written;
    }

    static string Done(List<DrawingFile> written, List<string> files) =>
        written.Count == 0 ? "" : $"; {string.Join(", ", files.Take(written.Count))} were written already";

    static void Saved(SaveOutcome outcome, string path, List<string> warnings)
    {
        warnings.AddRange(SolidWorksCodes.SaveWarnings(outcome.Warnings));
        if (outcome.Saved) return;
        var errors = SolidWorksCodes.SaveErrors(outcome.Errors);
        throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not export {path}: {string.Join(", ", errors.DefaultIfEmpty("no error reported"))}",
            new JsonObject
            {
                ["path"] = path,
                ["errors"] = errors.Select(n => (object?)n).ToList(),
                ["warnings"] = warnings.Select(n => (object?)n).ToList(),
            });
    }

    static DrawingExportFile Describe(DrawingFile file, string path, List<string> sheets) => new DrawingExportFile
    {
        Path = path,
        Sheets = sheets.ToList(),
        Bytes = file.Bytes,
        Header = file.Header,
        Pages = file.Pages.Select(p => new ExportedPage { Sheet = p.Sheet, WidthMm = p.Page.WidthMm, HeightMm = p.Page.HeightMm }).ToList(),
        Extents = file.Extents is DxfBox box
            ? new DrawingExtents
            {
                MinXMm = box.MinX, MinYMm = box.MinY, MaxXMm = box.MaxX, MaxYMm = box.MaxY,
                LengthMm = Math.Max(box.Width, box.Height), WidthMm = Math.Min(box.Width, box.Height), From = "its entities",
            }
            : null,
        View = file.View,
        ViewExtents = file.ViewExtents is DxfBox v ? new SheetBox { Min = new SheetPoint(v.MinX, v.MinY), Max = new SheetPoint(v.MaxX, v.MaxY) } : null,
    };

    /// <summary>The same document as if its unsaved-changes flag were clear: to compare the rest with what it was.</summary>
    static DocumentInfo Clean(DocumentInfo d) => new DocumentInfo { Title = d.Title, Path = d.Path, Type = d.Type, Dirty = false, Visible = d.Visible };

    protected override void Verify(ExportDrawingRequest request, ExportDrawingResult result, IOperationContext context)
    {
        foreach (var file in result.Files.Where(f => !File.Exists(f.Path)))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{file.Path} is gone after export.drawing wrote it",
                new JsonObject { ["check"] = "file_written", ["path"] = file.Path });
        }
        var (doc, _) = DocumentLocator.FindDrawing(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        ExportPartOperation.Unchanged(result.Document, doc.Info());
    }
}

/// <summary>
/// What a drawing holds, to tell that an export changed nothing in it: each sheet's size, scale and projection, each view's
/// place, scale and type, and how many annotations and tables the drawing has.
/// </summary>
public sealed class DrawingSnapshot
{
    readonly List<string> _lines;
    readonly int _sheets, _views, _annotations, _tables;

    DrawingSnapshot(List<string> lines, int sheets, int views, int annotations, int tables)
    {
        _lines = lines;
        _sheets = sheets;
        _views = views;
        _annotations = annotations;
        _tables = tables;
    }

    public static DrawingSnapshot Take(ISwDrawing drawing)
    {
        var lines = new List<string>();
        int views = 0;
        foreach (var sheet in drawing.SheetNames())
        {
            var s = drawing.ReadSheet(sheet);
            lines.Add(s == null ? $"sheet {sheet}: unreadable" : FormattableString.Invariant($"sheet {sheet}: {s.WidthMm:0.###} × {s.HeightMm:0.###} mm, {s.Scale}, {WireNames.ForEnum(s.Projection)}"));
            foreach (var name in drawing.ViewNames(sheet))
            {
                views++;
                var v = drawing.ReadView(name);
                lines.Add(v == null ? $"view {name}: unreadable" : $"view {name} on {sheet}: {WireNames.ForEnum(v.Type)} at {v.Position}, {v.Scale}");
            }
        }
        return new DrawingSnapshot(lines, lines.Count - views, views, DrawingAnnotations.Count(drawing), DrawingTables.Count(drawing));
    }

    /// <summary>verification_failed (drawing_unchanged), naming what differs, unless <paramref name="now"/> holds the same.</summary>
    public void Unchanged(DrawingSnapshot now, string title)
    {
        var gone = _lines.Except(now._lines).ToList();
        var come = now._lines.Except(_lines).ToList();
        var moved = new List<string>();
        moved.AddRange(gone.Select(l => $"was {l}"));
        moved.AddRange(come.Select(l => $"now {l}"));
        if (now._annotations != _annotations) moved.Add($"annotations {_annotations} -> {now._annotations}");
        if (now._tables != _tables) moved.Add($"tables {_tables} -> {now._tables}");
        if (moved.Count == 0) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            $"exporting changed {title}: {string.Join("; ", moved)}. It was not saved; undo the change or close it without saving",
            new JsonObject { ["check"] = "drawing_unchanged", ["expected"] = ToString(), ["measured"] = now.ToString(), ["changes"] = moved.Select(m => (object?)m).ToList() });
    }

    public override string ToString() => $"{_sheets} sheets, {_views} views, {_annotations} annotations and {_tables} tables (each sheet's size, scale and projection and each view's place, scale and type measured before and after)";
}
