using System;
using System.Collections.Generic;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>
/// How export.flat_pattern asks SOLIDWORKS for the flat pattern (design 454, Flat-pattern export: measure
/// IPartDoc.ExportToDWG2 and ExportFlatPatternView, and use the one that writes). ExportFlatPatternView is CADapter's
/// measured call (it wrote DXF and DWG on 2026 on a saved part), but the 6.D live run found it draws no bend lines
/// and writes the drawing sheet as the DXF's extents; ExportToDWG2 with swExportToDWG_ExportSheetMetal draws the
/// bend lines asked for, and is the only one that can add sketches. See <see cref="Methods"/>.
/// </summary>
public static class FlatPatternExports
{
    public const string FlatPatternView = "ExportFlatPatternView";
    public const string ToDwg2 = "ExportToDWG2";

    /// <summary>ExportToDWG2's sheet-metal option bits (SheetMetalOptions): the flat pattern's geometry, its bend lines, its sketches.</summary>
    public const int GeometryBit = 1;
    public const int BendLinesBit = 4;
    public const int SketchesBit = 8;

    public static int DwgOptions(bool bendLines, bool sketches) => GeometryBit | (bendLines ? BendLinesBit : 0) | (sketches ? SketchesBit : 0);

    /// <summary>
    /// The calls to try, in order, until one writes the file (6.D live run, SOLIDWORKS 2026 SP4). Sketches, or a part
    /// with unsaved changes: ExportToDWG2 alone (ExportFlatPatternView cannot add sketches, and on a part with unsaved
    /// changes it wrote the base alone, 100 × 60 for a flat of 100 × 82.0265, where ExportToDWG2 wrote the part as it
    /// is now). Bend lines: ExportToDWG2 first, because ExportFlatPatternView drew none when asked; if only it writes,
    /// the file's bend-line check refuses it. Neither: CADapter's ExportFlatPatternView first.
    /// </summary>
    public static IReadOnlyList<string> Methods(bool bendLines, bool sketches, bool unsaved) =>
        sketches || unsaved ? new[] { ToDwg2 } : bendLines ? new[] { ToDwg2, FlatPatternView } : new[] { FlatPatternView, ToDwg2 };

    /// <summary>How far each side of a DXF may be from the cut list's flat size: 0.01 mm beyond the cut list's own rounding.</summary>
    public static double ToleranceMm(IReadOnlyList<KeyValuePair<string, string>> cutList, double mmPerUnit) =>
        FlatPatternFileCheck.ExtentsToleranceMm + Math.Max(
            SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.LengthProperty, mmPerUnit),
            SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.WidthProperty, mmPerUnit));
}
