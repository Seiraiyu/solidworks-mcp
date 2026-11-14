using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The drawings suite's export cases (design 611, Export; plan 5.E): the block's drawing, as cases 1 to 35 left it (Sheet1
/// with the block's views, Details with the tab's flat pattern and the pin, Assembly with pinblock exploded), written as
/// PDF, DXF and DWG through export.drawing, each file read back here as well as by the add-in.
/// </summary>
public sealed partial class DrawingsSuite
{
    /// <summary>The longest export.drawing call, for the report's durations against the export family's 120 s.</summary>
    static TimeSpan _longestExport;

    async Task<ExportDrawingResult> ExportDrawing(ExportDrawingRequest request)
    {
        var clock = Stopwatch.StartNew();
        var result = await Call<ExportDrawingRequest, ExportDrawingResult>(request);
        if (clock.Elapsed > _longestExport) _longestExport = clock.Elapsed;
        return result;
    }

    /// <summary>The largest view on <paramref name="sheet"/> by its drawn extents, as drawing.read gives it.</summary>
    static ViewInfo Largest(SheetInfo sheet) =>
        sheet.Views.Where(v => v.Extents != null).OrderByDescending(v => (v.Extents!.Max.X - v.Extents.Min.X) * (v.Extents.Max.Y - v.Extents.Min.Y)).First();

    static string Head(string path, int bytes)
    {
        using var s = File.OpenRead(path);
        var buffer = new byte[Math.Min(bytes, (int)s.Length)];
        int read = s.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [SkippableFact, Step(36)]
    public Task A_pdf_of_the_drawing_has_a_page_per_sheet_each_its_sheets_size() => _run.CaseAsync(36, "the drawing saved; export.drawing pdf of every sheet: one file starting %PDF-, a page per sheet in order, each its sheet's size within 0.1 mm; the drawing's path kept, marked_dirty reported (and the drawing not saved behind us), no dialog", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = Drawing });
        var read = await Read();
        Assert.False(read.Document.Dirty, "the drawing has unsaved changes after document.save");
        var savedAt = File.GetLastWriteTimeUtc(Drawing);
        string path = _run.Guard.NewScratchPath("block", ".pdf");
        var result = await ExportDrawing(new ExportDrawingRequest { Document = Drawing, Path = path, Format = DrawingExportFormat.Pdf });
        Assert.Equal(read.Sheets.Select(s => s.Name), result.Sheets);
        var file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.StartsWith("%PDF-", Head(path, 8));
        Assert.Equal(read.Sheets.Count, file.Pages.Count);
        for (int i = 0; i < read.Sheets.Count; i++)
        {
            var sheet = read.Sheets[i];
            var page = file.Pages[i];
            Assert.Equal(sheet.Name, page.Sheet);
            bool straight = Math.Abs(page.WidthMm - sheet.WidthMm) <= 0.1 && Math.Abs(page.HeightMm - sheet.HeightMm) <= 0.1;
            bool turned = Math.Abs(page.WidthMm - sheet.HeightMm) <= 0.1 && Math.Abs(page.HeightMm - sheet.WidthMm) <= 0.1;
            Assert.True(straight || turned, $"page {i + 1} is {page.WidthMm} x {page.HeightMm}, {sheet.Name} {sheet.WidthMm} x {sheet.HeightMm}");
        }
        Assert.Equal(Drawing, result.Document.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(result.MarkedDirty, result.Document.Dirty);
        Assert.Equal(savedAt, File.GetLastWriteTimeUtc(Drawing));
        Assert.Null((await _run.AddinStateAsync()).Dialog);
        Made["pdf_marked_dirty"] = result.MarkedDirty.ToString();
        return $"{Path.GetFileName(path)}: {file.Bytes} bytes, {file.Header}, pages {string.Join(", ", file.Pages.Select(p => $"{p.Sheet} {p.WidthMm:0.##} x {p.HeightMm:0.##}"))}; " +
               $"marked_dirty {result.MarkedDirty}; preferences {string.Join(", ", result.Preferences)}; warnings {string.Join(", ", result.Warnings.DefaultIfEmpty("none"))}; {string.Join("; ", result.Checks)}";
    });

    [SkippableFact, Step(37)]
    public Task A_dxf_of_sheet1_holds_its_largest_view_where_the_drawing_draws_it() => _run.CaseAsync(37, "export.drawing dxf of Sheet1 while Assembly is shown: the path itself, its entities on the A3 sheet, the largest view's drawn extents (drawing.read's, sheet mm) found among them within 0.01 mm; Assembly shown again; then every sheet: a file per sheet named <name>-<sheet>.dxf, each an ASCII DXF", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Call<SheetActivateRequest, SheetActivateResult>(new SheetActivateRequest { Document = Drawing, Sheet = "Assembly" });
        var read = await Read();
        var sheet1 = read.Sheets.Single(s => s.Name == "Sheet1");
        var largest = Largest(sheet1);
        string path = _run.Guard.NewScratchPath("sheet1", ".dxf");
        var one = await ExportDrawing(new ExportDrawingRequest { Document = Drawing, Path = path, Format = DrawingExportFormat.Dxf, Sheets = new List<string> { "Sheet1" } });
        var file = Assert.Single(one.Files);
        Assert.Equal(path, file.Path);
        Assert.Contains("ENTITIES", File.ReadAllText(path));
        Assert.Equal(largest.Name, file.View);
        Box(file.ViewExtents, largest.Extents!.Min.X, largest.Extents.Max.X, largest.Extents.Min.Y, largest.Extents.Max.Y, "the largest view in the DXF");
        Assert.True(file.Extents!.MinXMm >= -0.1 && file.Extents.MaxXMm <= 420.1 && file.Extents.MinYMm >= -0.1 && file.Extents.MaxYMm <= 297.1, $"the DXF reaches off the sheet: {file.Extents.MinXMm}, {file.Extents.MinYMm} to {file.Extents.MaxXMm}, {file.Extents.MaxYMm}");
        Assert.Equal("Assembly", (await Read()).ActiveSheet);

        string all = _run.Guard.NewScratchPath("block", ".dxf");
        var every = await ExportDrawing(new ExportDrawingRequest { Document = Drawing, Path = all, Format = DrawingExportFormat.Dxf });
        Assert.Equal(read.Sheets.Select(s => Path.Combine(_run.Guard.ScratchDirectory, $"block-{s.Name}.dxf")), every.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(every.Files, f => Assert.Contains("ENTITIES", File.ReadAllText(f.Path)));
        Assert.Equal("Assembly", (await Read()).ActiveSheet);
        return $"{Path.GetFileName(path)}: {file.Bytes} bytes, entities {file.Extents.MinXMm:0.###}, {file.Extents.MinYMm:0.###} to {file.Extents.MaxXMm:0.###}, {file.Extents.MaxYMm:0.###}; {file.View} found at {file.ViewExtents} (drawn {largest.Extents}); " +
               $"every sheet: {string.Join(", ", every.Files.Select(f => $"{Path.GetFileName(f.Path)} ({f.View ?? "no view"})"))}; marked_dirty {every.MarkedDirty}; {string.Join("; ", one.Checks)}";
    });

    [SkippableFact, Step(38)]
    public Task A_dwg_of_details_starts_with_its_version() => _run.CaseAsync(38, "export.drawing dwg of Details: one file, the path itself, starting AC10xx (read here from its bytes) and at least 1 KB", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = _run.Guard.NewScratchPath("details", ".dwg");
        var result = await ExportDrawing(new ExportDrawingRequest { Document = Drawing, Path = path, Format = DrawingExportFormat.Dwg, Sheets = new List<string> { "Details" } });
        var file = Assert.Single(result.Files);
        string version = Head(path, 6);
        Assert.Matches("^AC10[0-9]{2}$", version);
        Assert.Equal(version, file.Header);
        Assert.True(new FileInfo(path).Length >= 1024, $"{path} is {new FileInfo(path).Length} bytes");
        return $"{Path.GetFileName(path)}: {version}, {file.Bytes} bytes; marked_dirty {result.MarkedDirty}";
    });

    [SkippableFact, Step(39)]
    public Task A_never_saved_drawing_and_a_part_are_refused_with_no_dialog() => _run.CaseAsync(39, "export.drawing of a never-saved drawing: invalid_argument before any SOLIDWORKS call, no Save As box, no file; of the block (a part): wrong_document_type naming export.part; export.part of the drawing: wrong_document_type naming export.drawing", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var unsaved = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Drawing });
        string pdf = _run.Guard.NewScratchPath("unsaved", ".pdf");
        try
        {
            var refused = await Refused(() => ExportDrawing(new ExportDrawingRequest { Document = unsaved.Document.Title, Path = pdf, Format = DrawingExportFormat.Pdf }));
            Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
            Assert.Equal(false, refused.Error.Details!["saved"]);
            Assert.Null((await _run.AddinStateAsync()).Dialog);
            Assert.False(File.Exists(pdf));
        }
        finally
        {
            // A document never saved has no path, so the guard would call it the owner's: it goes before the next case.
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = unsaved.Document.Title, Discard = true });
        }
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = Block });
        var part = await Refused(() => ExportDrawing(new ExportDrawingRequest { Document = Block, Path = pdf, Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.WrongDocumentType, part.Code);
        Assert.Contains("export.part", (string)part.Error.Details!["instead"]!);
        var drawing = await Refused(() => Call<ExportPartRequest, ExportPartResult>(new ExportPartRequest { Document = Drawing, Path = _run.Guard.NewScratchPath("drawing", ".step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.WrongDocumentType, drawing.Code);
        Assert.Contains("export.drawing", (string)drawing.Error.Details!["instead"]!);
        return $"never saved: refused, no dialog, no file; the block: {part.Message}; the drawing to export.part: {drawing.Message}; longest export.drawing {_longestExport.TotalMilliseconds:0} ms (export default 120 s)";
    });
}
