using System;
using System.Collections.Generic;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>
/// How export.flat_pattern asks SOLIDWORKS for the flat pattern (design 454, Flat-pattern export: measure
/// IPartDoc.ExportToDWG2 and ExportFlatPatternView, and use the one that writes). ExportFlatPatternView is CADapter's
/// measured call (it wrote DXF and DWG on 2026 on a saved part), so it is tried first; ExportToDWG2 with
/// swExportToDWG_ExportSheetMetal is tried when it writes nothing, and is the only one that can add sketches.
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

    /// <summary>The calls to try, in order, until one writes the file.</summary>
    public static IReadOnlyList<string> Methods(bool sketches) => sketches ? new[] { ToDwg2 } : new[] { FlatPatternView, ToDwg2 };

    /// <summary>How far each side of a DXF may be from the cut list's flat size: 0.01 mm beyond the cut list's own rounding.</summary>
    public static double ToleranceMm(IReadOnlyList<KeyValuePair<string, string>> cutList, double mmPerUnit) =>
        FlatPatternFileCheck.ExtentsToleranceMm + Math.Max(
            SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.LengthProperty, mmPerUnit),
            SheetMetalParts.HalfUnitMm(cutList, SheetMetalParts.WidthProperty, mmPerUnit));
}
