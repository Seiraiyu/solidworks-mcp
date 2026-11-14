using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// A drawing's exports as SOLIDWORKS writes them: a PDF with a page per sheet asked for, each its sheet's size; a DXF of the
/// sheet shown, in sheet mm, with its border and each view's drawn extents as four lines; a DWG header. The DXF follows the
/// preferences at the time of the call: 1:1 output (swDxfOutputNoScale 1) writes it at twice the size, and views as blocks
/// (swDxfExportViewAsBlock) writes them as INSERTs, which are not measured.
/// </summary>
sealed partial class FakeDrawing
{
    const int OutputNoScale = 136, ViewAsBlock = 806, MultiSheet = 253, PdfViewOnSave = 617;

    /// <summary>Each PDF export's path, sheets and swPDFViewOnSave at the time.</summary>
    public List<(string Path, List<string> Sheets, double ViewOnSave)> PdfExports { get; } = new List<(string, List<string>, double)>();

    /// <summary>The sheet shown at each DXF or DWG export, in order.</summary>
    public List<string> SheetsExported { get; } = new List<string>();

    /// <summary>When true, ExportPdf reports SOLIDWORKS' generic save error and writes nothing.</summary>
    public bool PdfFails { get; set; }

    /// <summary>When set, an export of a saved drawing marks it as having unsaved changes (SOLIDWORKS 2026 SP4's exports may).</summary>
    public bool ExportMarksDirty { get; set; }

    /// <summary>When set, the PDF leaves out the last sheet asked for.</summary>
    public bool PdfDropsLastSheet { get; set; }

    /// <summary>When set, each view is written this far (sheet mm, x) from where the drawing draws it.</summary>
    public double DxfViewShiftMm { get; set; }

    /// <summary>When set, each export deletes the first view of the sheet shown (a drawing changed by an export).</summary>
    public bool ExportDeletesAView { get; set; }

    /// <summary>The preferences an export reads; set by the test.</summary>
    public FakePreferences? Prefs { get; set; }

    public SaveOutcome ExportPdf(string path, IReadOnlyList<string> sheets)
    {
        Calls.Add($"export pdf {string.Join(", ", sheets)}");
        PdfExports.Add((path, sheets.ToList(), (Prefs ?? new FakePreferences()).Get(PreferenceKind.Toggle, PdfViewOnSave)));
        if (PdfFails) return new SaveOutcome(false, 1, 0);
        var written = PdfDropsLastSheet ? sheets.Take(sheets.Count - 1).ToList() : sheets.ToList();
        File.WriteAllBytes(path, Pdf(written.Select(s => Sheet(s).Facts).ToList()));
        Exported();
        return new SaveOutcome(true, 0, 0);
    }

    /// <summary>What FakeDocument.Export writes for this drawing at <paramref name="path"/>: the sheet shown, as DXF or DWG.</summary>
    public byte[] ExportBytes(string path)
    {
        string sheet = Active ?? Sheets[0].Facts.Name;
        SheetsExported.Add(sheet);
        Calls.Add($"export {Path.GetExtension(path).ToLowerInvariant()} {sheet}");
        byte[] bytes = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? Encoding.ASCII.GetBytes("AC1032").Concat(new byte[2048]).ToArray()
            : Encoding.ASCII.GetBytes(Dxf(Sheet(sheet)));
        Exported();
        return bytes;
    }

    void Exported()
    {
        if (ExportMarksDirty && _doc.Document.Path != null) _doc.Document.Dirty = true;
        if (ExportDeletesAView)
        {
            var sheet = Sheet(Active ?? Sheets[0].Facts.Name);
            if (sheet.Views.Count > 0) sheet.Views.RemoveAt(0);
        }
    }

    string Dxf(FakeSheet sheet)
    {
        var prefs = Prefs ?? new FakePreferences();
        if (prefs.Get(PreferenceKind.Integer, MultiSheet) != 0) throw new InvalidOperationException("the fake writes the shown sheet only: swDxfMultiSheetOption was not set to the active sheet");
        double k = prefs.Get(PreferenceKind.Integer, OutputNoScale) == 1 ? 2 : 1;
        bool blocks = prefs.Get(PreferenceKind.Toggle, ViewAsBlock) == 1;
        var text = new StringBuilder();
        void Pair(int code, object value) => text.Append(code).Append("\r\n").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");
        void Rect(double x0, double y0, double x1, double y1)
        {
            foreach (var (a, b, c, d) in new[] { (x0, y0, x1, y0), (x1, y0, x1, y1), (x1, y1, x0, y1), (x0, y1, x0, y0) })
            {
                Pair(0, "LINE");
                Pair(8, "0");
                Pair(10, a * k);
                Pair(20, b * k);
                Pair(11, c * k);
                Pair(21, d * k);
            }
        }
        Pair(0, "SECTION");
        Pair(2, "HEADER");
        Pair(9, "$INSUNITS");
        Pair(70, 4);
        Pair(0, "ENDSEC");
        Pair(0, "SECTION");
        Pair(2, "ENTITIES");
        Rect(0, 0, sheet.Facts.WidthMm, sheet.Facts.HeightMm);
        foreach (var view in sheet.Views)
        {
            if (!(DrawingReader.Extents(this, ReadView(view.Facts.Name)!) is SheetBox box)) continue;
            if (blocks)
            {
                Pair(0, "INSERT");
                Pair(2, view.Facts.Name);
                continue;
            }
            Rect(box.Min.X + DxfViewShiftMm, box.Min.Y, box.Max.X + DxfViewShiftMm, box.Max.Y);
        }
        Pair(0, "ENDSEC");
        Pair(0, "EOF");
        return text.ToString();
    }

    /// <summary>A PDF 1.4 with a page per sheet, each its sheet's size in points.</summary>
    static byte[] Pdf(IReadOnlyList<SheetFacts> sheets)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {sheets.Count} /Kids [{string.Join(" ", sheets.Select((_, i) => $"{i + 3} 0 R"))}] >>",
        };
        objects.AddRange(sheets.Select(s => FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {s.WidthMm / PdfFile.MmPerPoint:0.###} {s.HeightMm / PdfFile.MmPerPoint:0.###}] >>")));
        var text = new StringBuilder("%PDF-1.4\n");
        for (int i = 0; i < objects.Count; i++) text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        text.Append("trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
