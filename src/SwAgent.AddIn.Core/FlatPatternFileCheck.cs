using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>What the flat pattern's file must show: the flat size the cut list gives, and whether bend lines were asked for.</summary>
public sealed class FlatPatternExpectation
{
    /// <summary>The flat size from the cut list, mm (the order does not matter: the drawing may be turned).</summary>
    public double LengthMm { get; set; }

    public double WidthMm { get; set; }

    /// <summary>How far each side of the drawing may be from the flat size, mm.</summary>
    public double ToleranceMm { get; set; }

    /// <summary>Millimetres per unit of the part, for a DXF that does not say its units.</summary>
    public double MmPerUnit { get; set; } = 1;

    /// <summary>Bend lines were asked for.</summary>
    public bool BendLines { get; set; }

    /// <summary>The part's visible sketches were asked for: their free lines cannot be told from bend lines.</summary>
    public bool Sketches { get; set; }

    /// <summary>How many bends the part has (a part without bends has no bend lines either way).</summary>
    public int Bends { get; set; }
}

/// <summary>What the flat pattern's file holds, as read back from disk.</summary>
public sealed class FlatPatternFile
{
    public long Bytes { get; set; }

    /// <summary>DXF, or the DWG's version string (AC1032 and the like).</summary>
    public string Header { get; set; } = "";

    /// <summary>A DXF's drawing extents in mm; null for a DWG.</summary>
    public DxfBox? Extents { get; set; }

    /// <summary>Where the extents came from: $EXTMIN/$EXTMAX, or its entities.</summary>
    public string? ExtentsFrom { get; set; }

    /// <summary>A DXF's bend-line entities; null for a DWG.</summary>
    public int? BendLines { get; set; }

    /// <summary>A DXF's layers, each as name = entities on it.</summary>
    public List<string> Layers { get; } = new List<string>();

    /// <summary>A DXF's linetypes, each as name = entities drawn with it.</summary>
    public List<string> Linetypes { get; } = new List<string>();

    public List<string> Checks { get; } = new List<string>();
}

/// <summary>
/// Proves on disk that export.flat_pattern wrote the flat pattern (design 454, Flat-pattern export): the file was
/// written by this call and is not empty; a DXF parses, and the extents of its entities match the flat size from the
/// cut list, turned or not (the header's $EXTMIN/$EXTMAX is reported, not compared: SOLIDWORKS may write the sheet
/// there); with bend lines asked for it holds a line across the outline for each bend (<see cref="DxfFile.BendLines"/>),
/// and without, none. A DWG starts with its AC10xx version and is at least
/// <see cref="DwgLeastBytes"/> long; its extents are not read. Otherwise verification_failed naming the check, what
/// was expected and what was found.
/// </summary>
public static class FlatPatternFileCheck
{
    /// <summary>How far a DXF's side may be from the flat size beyond the cut list's own rounding (design 454: 0.01 mm).</summary>
    public const double ExtentsToleranceMm = 0.01;

    /// <summary>A DWG holding a flat pattern is at least this long; its header alone is a few hundred bytes.</summary>
    public const int DwgLeastBytes = 1024;

    static readonly Regex DwgVersion = new Regex(@"^AC10\d\d$", RegexOptions.CultureInvariant);

    public static FlatPatternFile Check(string path, FlatPatternFormat format, DateTime startedUtc, FlatPatternExpectation expected)
    {
        var written = ExportFileCheck.Written(path, startedUtc);
        var file = new FlatPatternFile { Bytes = written.Bytes };
        file.Checks.AddRange(written.Checks);
        if (format == FlatPatternFormat.Dwg)
            Dwg(path, file);
        else
            Dxf(path, expected, file);
        return file;
    }

    static void Dwg(string path, FlatPatternFile file)
    {
        byte[] head = new byte[6];
        int read;
        using (var s = File.OpenRead(path)) read = s.Read(head, 0, head.Length);
        string version = Encoding.ASCII.GetString(head, 0, read);
        if (!DwgVersion.IsMatch(version))
            throw ExportFileCheck.Failed("file_header", path, "AC10xx", ExportFileCheck.Snippet(version), $"{path} is not a DWG: it does not start with an AC10xx version");
        if (file.Bytes < DwgLeastBytes)
            throw ExportFileCheck.Failed("dwg_size", path, $"at least {DwgLeastBytes} bytes", $"{file.Bytes} bytes", $"{path} is a DWG of {file.Bytes} bytes, too small to hold a flat pattern");
        file.Header = version;
        file.Checks.Add($"a DWG ({version}) of {file.Bytes} bytes; its extents are not read (no DWG reader)");
    }

    static void Dxf(string path, FlatPatternExpectation expected, FlatPatternFile file)
    {
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
        file.Layers.AddRange(dxf.Layers.Select(l => $"{l.Key} = {l.Value}"));
        file.Linetypes.AddRange(dxf.Linetypes.Select(l => $"{l.Key} = {l.Value}"));

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
            unit = expected.MmPerUnit;
            units = FormattableString.Invariant($"no $INSUNITS: the part's own unit, {unit} mm");
        }

        // The flat is what the entities fill. The header is reported, never compared: the 6.D live run found
        // ExportFlatPatternView writes the drawing sheet (279.4 × 215.9, letter) there, not the flat.
        if (!(dxf.EntityExtents?.Scaled(unit) is DxfBox box))
            throw ExportFileCheck.Failed("dxf_extents", path, "drawing extents", "none", $"{path} has no lines, arcs, circles, polylines, splines or ellipses to measure");
        file.ExtentsFrom = "its entities";
        file.Extents = box;
        file.BendLines = dxf.BendLines;
        string splines = dxf.Splines > 0 ? $", {dxf.Splines} of them splines bounded by their control points" : "";
        string skipped = dxf.Skipped.Count > 0 ? $"; not measured: {string.Join(", ", dxf.Skipped.Select(s => $"{s.Value} {s.Key}"))}" : "";
        file.Checks.Add($"an ASCII DXF of {dxf.Entities} measured entities{splines}{skipped}; {units}; layers {string.Join(", ", file.Layers)}");
        file.Checks.Add(dxf.HeaderExtents?.Scaled(unit) is DxfBox header
            ? FormattableString.Invariant($"its header's $EXTMIN/$EXTMAX give {Math.Max(header.Width, header.Height):0.####} × {Math.Min(header.Width, header.Height):0.####} mm ({header}): reported, not compared (SOLIDWORKS may write the sheet there)")
            : "its header gives no $EXTMIN/$EXTMAX");

        double wantLong = Math.Max(expected.LengthMm, expected.WidthMm), wantShort = Math.Min(expected.LengthMm, expected.WidthMm);
        double gotLong = Math.Max(box.Width, box.Height), gotShort = Math.Min(box.Width, box.Height);
        double tolerance = expected.ToleranceMm;
        string want = FormattableString.Invariant($"{wantLong:0.####} × {wantShort:0.####} mm");
        string got = FormattableString.Invariant($"{gotLong:0.####} × {gotShort:0.####} mm");
        if (Math.Abs(gotLong - wantLong) > tolerance || Math.Abs(gotShort - wantShort) > tolerance)
        {
            throw ExportFileCheck.Failed("flat_size", path, FormattableString.Invariant($"{want} within {tolerance:0.####} mm"), got,
                FormattableString.Invariant($"{path}'s entities reach {got}, not the flat size {want} from the cut list (within {tolerance:0.####} mm)"));
        }
        file.Checks.Add(FormattableString.Invariant($"its extents {got} ({file.ExtentsFrom}, {box}) match the flat size {want} from the cut list within {tolerance:0.####} mm"));

        string found = $"{dxf.BendLines} lines across the outline ({dxf.NamedBendLines} entities on a layer or linetype named *{DxfFile.BendMarker}*); layers {string.Join(", ", file.Layers)}; linetypes {string.Join(", ", file.Linetypes)}";
        if (!expected.BendLines && expected.Sketches)
        {
            file.Checks.Add(dxf.BendLines == 0
                ? "no bend lines, as asked"
                : $"{dxf.BendLines} free lines, asked without bend lines and with sketches: a sketch's lines cannot be told from bend lines, so they are reported, not checked");
        }
        else if (!expected.BendLines)
        {
            if (dxf.BendLines > 0)
                throw ExportFileCheck.Failed("bend_lines", path, "no bend lines", found, $"{path} was asked without bend lines, and it has {dxf.BendLines}");
            file.Checks.Add("no bend lines, as asked");
        }
        else if (expected.Bends == 0)
        {
            file.Checks.Add("the part has no bends, so it has no bend lines to show");
        }
        else if (dxf.BendLines < expected.Bends)
        {
            throw ExportFileCheck.Failed("bend_lines", path, $"a bend line for each of {expected.Bends} bends", found,
                $"{path} was asked with bend lines, and has {dxf.BendLines} for the part's {expected.Bends} bends ({found})");
        }
        else
        {
            file.Checks.Add($"{dxf.BendLines} bend lines across the outline for {expected.Bends} bends");
        }
    }
}
