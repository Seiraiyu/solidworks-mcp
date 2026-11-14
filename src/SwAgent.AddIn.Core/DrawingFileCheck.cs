using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>A sheet as export.drawing expects to find it in a file: its size, sheet mm, and its views' drawn extents.</summary>
public sealed class SheetExpectation
{
    public string Name { get; set; } = "";

    public double WidthMm { get; set; }

    public double HeightMm { get; set; }

    /// <summary>Its views that draw something, with their drawn extents (drawing.read's extents).</summary>
    public List<ViewExpectation> Views { get; set; } = new List<ViewExpectation>();

    public override string ToString() => FormattableString.Invariant($"{Name} ({WidthMm:0.##} × {HeightMm:0.##} mm)");
}

/// <summary>A view's drawn extents on its sheet, sheet mm, and the scale it is drawn at.</summary>
public sealed class ViewExpectation
{
    public string Name { get; set; } = "";

    public SheetBox Extents { get; set; } = new SheetBox();

    /// <summary>Its scale as a number: 1:2 is 0.5.</summary>
    public double Scale { get; set; } = 1;

    public double Area => (Extents.Max.X - Extents.Min.X) * (Extents.Max.Y - Extents.Min.Y);
}

/// <summary>A PDF page and the sheet it shows.</summary>
public sealed class DrawingFilePage
{
    public DrawingFilePage(string sheet, PdfPage page)
    {
        Sheet = sheet;
        Page = page;
    }

    public string Sheet { get; }

    public PdfPage Page { get; }
}

/// <summary>What a file export.drawing wrote holds, as read back from disk.</summary>
public sealed class DrawingFile
{
    public long Bytes { get; set; }

    /// <summary>%PDF- and its version, DXF, or the DWG's version.</summary>
    public string Header { get; set; } = "";

    /// <summary>A PDF's pages, each with the sheet it shows; empty otherwise.</summary>
    public List<DrawingFilePage> Pages { get; } = new List<DrawingFilePage>();

    /// <summary>A DXF's entity extents, mm; null otherwise.</summary>
    public DxfBox? Extents { get; set; }

    /// <summary>A DXF's largest view, found among its entities; null when its sheet has none, and for PDF and DWG.</summary>
    public string? View { get; set; }

    /// <summary>What that view's entities fill in the file, mm.</summary>
    public DxfBox? ViewExtents { get; set; }

    public List<string> Checks { get; } = new List<string>();
}

/// <summary>
/// Proves on disk that export.drawing wrote what was asked (design 611, Export): each file was written by this call and is
/// not empty; a PDF reads (<see cref="PdfFile"/>), has one page per sheet, each page its sheet's size within
/// <see cref="PageToleranceMm"/> (turned or not); a DXF reads (<see cref="DxfFile"/>), every entity lies on its sheet within
/// <see cref="SheetToleranceMm"/>, and the entities lying inside the sheet's largest view's drawn extents fill exactly
/// those extents, within <see cref="ViewToleranceMm"/> × the view's scale (at least <see cref="ViewToleranceMm"/>); a DWG
/// starts with its AC10xx version and is at least <see cref="DwgFile.LeastBytes"/> long. Otherwise verification_failed
/// naming the check, what was expected and what was found.
/// </summary>
public static class DrawingFileCheck
{
    /// <summary>A PDF page against its sheet, mm (design 611: 0.1 mm).</summary>
    public const double PageToleranceMm = 0.1;

    /// <summary>How far an entity may stand off its sheet, mm: a border drawn on the sheet's edge.</summary>
    public const double SheetToleranceMm = 0.1;

    /// <summary>A view's drawn extents against what its entities fill in the DXF, mm at 1:1 (design 611: 0.01 mm).</summary>
    public const double ViewToleranceMm = 0.01;

    public static DrawingFile Pdf(string path, DateTime startedUtc, IReadOnlyList<SheetExpectation> sheets)
    {
        var file = Written(path, startedUtc);
        PdfFile pdf;
        try
        {
            pdf = PdfFile.Read(path);
        }
        catch (FormatException e)
        {
            throw ExportFileCheck.Failed("file_header", path, "a PDF that can be read", e.Message, $"{path} is not a PDF that can be read: {e.Message}");
        }
        file.Header = "%PDF-" + pdf.Version;
        if (pdf.Pages.Count != sheets.Count)
        {
            throw ExportFileCheck.Failed("pdf_pages", path, sheets.Count.ToString(), pdf.Pages.Count.ToString(),
                $"{path} has {pdf.Pages.Count} pages for the {sheets.Count} sheets exported ({string.Join(", ", sheets.Select(s => s.Name))})");
        }
        file.Checks.Add($"a PDF {pdf.Version} of {pdf.Pages.Count} pages for {sheets.Count} sheets" + (pdf.ObjectStreams ? " (its pages read from compressed object streams)" : "") +
            (pdf.DeclaredCount is int declared && declared != pdf.Pages.Count ? $"; its page tree says {declared}" : ""));
        for (int i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var page = pdf.Pages[i];
            bool straight = Near(page.WidthMm, sheet.WidthMm, PageToleranceMm) && Near(page.HeightMm, sheet.HeightMm, PageToleranceMm);
            bool turned = Near(page.WidthMm, sheet.HeightMm, PageToleranceMm) && Near(page.HeightMm, sheet.WidthMm, PageToleranceMm);
            string size = $"{Mm(page.WidthMm)} × {Mm(page.HeightMm)} mm";
            string wanted = $"{Mm(sheet.WidthMm)} × {Mm(sheet.HeightMm)} mm";
            if (!straight && !turned)
            {
                throw ExportFileCheck.Failed("page_size", path, FormattableString.Invariant($"{wanted} within {PageToleranceMm} mm"), size,
                    $"page {i + 1} of {path} is {size}, not {sheet.Name}'s {wanted} (within {Mm(PageToleranceMm)} mm, turned or not)");
            }
            file.Pages.Add(new DrawingFilePage(sheet.Name, page));
            file.Checks.Add($"page {i + 1} ({sheet.Name}) {size}, its sheet {wanted}{(straight ? "" : ", turned")}" + (page.Rotate != 0 ? $" (/Rotate {page.Rotate})" : ""));
        }
        return file;
    }

    public static DrawingFile Dxf(string path, DateTime startedUtc, SheetExpectation sheet)
    {
        var file = Written(path, startedUtc);
        DxfFile dxf;
        try
        {
            dxf = DxfFile.Read(path);
        }
        catch (FormatException e)
        {
            throw ExportFileCheck.Failed("file_header", path, "an ASCII DXF", e.Message, $"{path} is not a DXF that can be read: {e.Message}");
        }
        file.Header = "DXF";
        double unit;
        string units;
        if (dxf.Units is int u && u != 0)
        {
            if (!DxfFile.TryMmPer(u, out unit))
                throw ExportFileCheck.Failed("dxf_units", path, "$INSUNITS of in, ft, mm, cm or m", $"$INSUNITS {u}", $"{path} is drawn in units {u} ($INSUNITS), which are not read here");
            units = FormattableString.Invariant($"$INSUNITS {u}, {unit} mm each");
        }
        else
        {
            unit = 1;
            units = "no $INSUNITS: taken as mm, the sheet's";
        }
        if (!(dxf.EntityExtents?.Scaled(unit) is DxfBox all))
            throw ExportFileCheck.Failed("dxf_extents", path, "entities", "none", $"{path} has no lines, arcs, circles, polylines, splines or ellipses to measure");
        file.Extents = all;
        string skipped = dxf.Skipped.Count > 0 ? $"; not measured: {string.Join(", ", dxf.Skipped.Select(s => $"{s.Value} {s.Key}"))}" : "";
        file.Checks.Add($"an ASCII DXF of {dxf.Entities} measured entities{skipped}; {units}");

        double t = SheetToleranceMm;
        if (all.MinX < -t || all.MinY < -t || all.MaxX > sheet.WidthMm + t || all.MaxY > sheet.HeightMm + t)
        {
            throw ExportFileCheck.Failed("dxf_on_sheet", path, FormattableString.Invariant($"within (0, 0) to ({sheet.WidthMm:0.##}, {sheet.HeightMm:0.##}) mm, {t} mm either side"), all.ToString(),
                $"{path}'s entities reach {all}, off {sheet} (within {Mm(t)} mm); SOLIDWORKS may have written it at another scale or origin");
        }
        file.Checks.Add($"its entities {all} lie on {sheet}");

        var largest = sheet.Views.OrderByDescending(v => v.Area).FirstOrDefault();
        if (largest == null)
        {
            file.Checks.Add($"{sheet.Name} has no view that draws anything, so none was sought");
            return file;
        }
        double tolerance = ViewToleranceMm * Math.Max(1, largest.Scale);
        var box = largest.Extents;
        var inside = dxf.EntityBoxes.Select(b => b.Scaled(unit))
            .Where(b => b.MinX >= box.Min.X - tolerance && b.MinY >= box.Min.Y - tolerance && b.MaxX <= box.Max.X + tolerance && b.MaxY <= box.Max.Y + tolerance).ToList();
        string expected = $"{Box(box)} within {Mm(tolerance)} mm";
        if (inside.Count == 0)
        {
            throw ExportFileCheck.Failed("view_in_file", path, expected, "no entity inside it",
                $"{path} has no entity inside {largest.Name}'s drawn extents {Box(box)} (within {Mm(tolerance)} mm): the view is not where the drawing draws it");
        }
        var found = new DxfBox(inside.Min(b => b.MinX), inside.Min(b => b.MinY), inside.Max(b => b.MaxX), inside.Max(b => b.MaxY));
        if (!Near(found.MinX, box.Min.X, tolerance) || !Near(found.MinY, box.Min.Y, tolerance) || !Near(found.MaxX, box.Max.X, tolerance) || !Near(found.MaxY, box.Max.Y, tolerance))
        {
            throw ExportFileCheck.Failed("view_in_file", path, expected, found.ToString(),
                $"the {inside.Count} entities of {path} inside {largest.Name}'s drawn extents {Box(box)} fill {found}, not those extents (within {Mm(tolerance)} mm)");
        }
        file.View = largest.Name;
        file.ViewExtents = found;
        file.Checks.Add($"{largest.Name}'s drawn extents {Box(box)}, the largest view on {sheet.Name}, are what its {inside.Count} entities fill ({found}) within {Mm(tolerance)} mm");
        return file;
    }

    public static DrawingFile Dwg(string path, DateTime startedUtc)
    {
        var file = Written(path, startedUtc);
        file.Header = DwgFile.Check(path, file.Bytes, "a drawing sheet", file.Checks);
        return file;
    }

    static DrawingFile Written(string path, DateTime startedUtc)
    {
        var written = ExportFileCheck.Written(path, startedUtc);
        var file = new DrawingFile { Bytes = written.Bytes };
        file.Checks.AddRange(written.Checks);
        return file;
    }

    static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance + 1e-9;

    static string Mm(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    static string Box(SheetBox b) => FormattableString.Invariant($"({b.Min.X:0.####}, {b.Min.Y:0.####}) to ({b.Max.X:0.####}, {b.Max.Y:0.####})");
}

/// <summary>A DWG as the exports check it: it starts with its AC10xx version and is at least <see cref="LeastBytes"/> long; its contents are not read (no DWG reader).</summary>
public static class DwgFile
{
    /// <summary>A DWG holding a drawing is at least this long; its header alone is a few hundred bytes.</summary>
    public const int LeastBytes = 1024;

    static readonly Regex Version = new Regex(@"^AC10\d\d$", RegexOptions.CultureInvariant);

    /// <summary>The version it starts with, after adding the check to <paramref name="checks"/>; verification_failed otherwise.</summary>
    public static string Check(string path, long bytes, string holding, List<string> checks)
    {
        byte[] head = new byte[6];
        int read;
        using (var s = File.OpenRead(path)) read = s.Read(head, 0, head.Length);
        string version = Encoding.ASCII.GetString(head, 0, read);
        if (!Version.IsMatch(version))
            throw ExportFileCheck.Failed("file_header", path, "AC10xx", ExportFileCheck.Snippet(version), $"{path} is not a DWG: it does not start with an AC10xx version");
        if (bytes < LeastBytes)
            throw ExportFileCheck.Failed("dwg_size", path, $"at least {LeastBytes} bytes", $"{bytes} bytes", $"{path} is a DWG of {bytes} bytes, too small to hold {holding}");
        checks.Add($"a DWG ({version}) of {bytes} bytes; its extents are not read (no DWG reader)");
        return version;
    }
}
