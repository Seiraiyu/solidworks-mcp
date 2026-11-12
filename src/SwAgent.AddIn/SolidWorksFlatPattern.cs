using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.AddIn;

/// <summary>
/// The flat pattern written to a file (design 454, Flat-pattern export): IPartDoc.ExportFlatPatternView, CADapter's
/// measured call, and IPartDoc.ExportToDWG2 with swExportToDWG_ExportSheetMetal. Neither answer is evidence (CADapter:
/// ExportFlatPatternView "fails inconsistently"); export.flat_pattern reads the file back. Both are called only on a
/// part saved to disk: on a part never saved CADapter measured a Save As box that waits for the user.
/// Proven by the sheet-metal live suite, not by unit tests.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    IPartDoc PartDoc => (IPartDoc)Doc;

    public bool ExportFlatPatternView(string path, bool bendLines) =>
        PartDoc.ExportFlatPatternView(path, (int)(bendLines
            ? swExportFlatPatternViewOptions_e.swExportFlatPatternOption_None
            : swExportFlatPatternViewOptions_e.swExportFlatPatternOption_RemoveBends));

    public bool ExportToDwg2(string path, string partPath, int sheetMetalOptions)
    {
        // The alignment SOLIDWORKS' own sheet-metal sample passes: the origin, then the x, y and z directions.
        object alignment = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        return PartDoc.ExportToDWG2(path, partPath, (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal, true, alignment, false, false, sheetMetalOptions, null);
    }
}
