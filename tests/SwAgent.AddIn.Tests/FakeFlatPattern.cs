using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// The flat pattern written as SOLIDWORKS 2026 SP4 writes it (6.D live run), on a <see cref="FakeDocument"/>'s sheet:
/// a DXF of the cut list's flat size (a rectangle in mm) with, from ExportToDWG2 and bend lines asked for, a line per
/// bend across it on layer 0, its header the flat; from ExportFlatPatternView no bend lines and the letter sheet as
/// its header; or a DWG of an AC1032 header and 4 KB. What a test sets makes it write nothing, draw it off size, leave
/// out the bend lines, or change the part.
/// </summary>
sealed partial class FakeDocument
{
    /// <summary>Each flat-pattern export: the call made, its path, and bend lines and sketches as asked.</summary>
    public List<(string Method, string Path, bool BendLines, bool Sketches)> FlatExports { get; } = new List<(string, string, bool, bool)>();

    /// <summary>The calls that answer false and write nothing.</summary>
    public HashSet<string> FlatExportFails { get; } = new HashSet<string>();

    /// <summary>The calls that answer true and write nothing.</summary>
    public HashSet<string> FlatExportWritesNothing { get; } = new HashSet<string>();

    /// <summary>The drawing comes out this much longer than the flat size, mm.</summary>
    public double FlatExportOffMm { get; set; }

    /// <summary>The drawing leaves out the bend lines even when asked for.</summary>
    public bool FlatExportDropsBendLines { get; set; }

    /// <summary>The export leaves the part flattened (Flat-Pattern1 unsuppressed).</summary>
    public bool FlatExportFlattens { get; set; }

    /// <summary>The export flips the part's unsaved-changes flag (as saving it, or changing it, would).</summary>
    public bool FlatExportTogglesDirty { get; set; }

    public bool ExportFlatPatternView(string path, bool bendLines) => WriteFlat(FlatPatternExports.FlatPatternView, path, bendLines, sketches: false);

    public bool ExportToDwg2(string path, string partPath, int sheetMetalOptions)
    {
        if (!string.Equals(partPath, Document.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"ExportToDWG2 was given {partPath}, not the part's path {Document.Path}");
        return WriteFlat(FlatPatternExports.ToDwg2, path, (sheetMetalOptions & FlatPatternExports.BendLinesBit) != 0, (sheetMetalOptions & FlatPatternExports.SketchesBit) != 0);
    }

    bool WriteFlat(string method, string path, bool bendLines, bool sketches)
    {
        FlatExports.Add((method, path, bendLines, sketches));
        if (FlatExportFails.Contains(method)) return false;
        if (FlatExportFlattens) Suppress(Tree.Single(f => f.Type == SheetMetalParts.FlatPatternType).Feature, false);
        if (FlatExportTogglesDirty) Document.Dirty = !Document.Dirty;
        if (FlatExportWritesNothing.Contains(method)) return true;
        if (path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = new byte[4096];
            Encoding.ASCII.GetBytes("AC1032").CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);
            return true;
        }
        var size = SheetMetalParts.FlatSize(CutList(), MmPerUnit()) ?? (0.0, 0.0);
        double w = size.LengthMm + FlatExportOffMm, h = size.WidthMm;
        var text = new StringBuilder();
        void Pair(int code, object value) => text.Append(code.ToString(CultureInfo.InvariantCulture)).Append("\r\n").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");
        void Line(double x1, double y1, double x2, double y2, string layer)
        {
            Pair(0, "LINE"); Pair(8, layer); Pair(10, x1); Pair(20, y1); Pair(11, x2); Pair(21, y2);
        }
        // As the 6.D live run measured on 2026 SP4: ExportFlatPatternView writes the letter sheet as $EXTMIN/$EXTMAX and
        // no bend lines; ExportToDWG2 writes the flat as its extents and its bend lines on layer 0, like the outline.
        bool view = method == FlatPatternExports.FlatPatternView;
        Pair(0, "SECTION"); Pair(2, "HEADER");
        Pair(9, "$INSUNITS"); Pair(70, 4);
        Pair(9, "$EXTMIN"); Pair(10, 0.0); Pair(20, 0.0);
        Pair(9, "$EXTMAX"); Pair(10, view ? 279.4 : w); Pair(20, view ? 215.9 : h);
        Pair(0, "ENDSEC"); Pair(0, "SECTION"); Pair(2, "ENTITIES");
        Line(0, 0, w, 0, "0"); Line(w, 0, w, h, "0"); Line(w, h, 0, h, "0"); Line(0, h, 0, 0, "0");
        if (bendLines && !view && !FlatExportDropsBendLines)
        {
            for (int i = 0; i < FakeBends.Count; i++) Line(0, h * (i + 1) / (FakeBends.Count + 1), w, h * (i + 1) / (FakeBends.Count + 1), "0");
        }
        Pair(0, "ENDSEC"); Pair(0, "EOF");
        File.WriteAllText(path, text.ToString());
        return true;
    }
}
