using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.AddIn;

/// <summary>A drawing's sheets written as PDF (design 611, Export). Covered by the drawings live suite, not by unit tests.</summary>
internal sealed partial class SolidWorksDrawing
{
    public SaveOutcome ExportPdf(string path, IReadOnlyList<string> sheets)
    {
        int errors = 0, warnings = 0;
        if (!(_sw.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) is IExportPdfData data))
            return new SaveOutcome(false, (int)swFileSaveError_e.swGenericSaveError, 0);
        // Sheet names are strings: SetSheets takes them as a string array (design 611, Constraints: never an array of objects).
        if (!data.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportSpecifiedSheets, sheets.ToArray()))
            return new SaveOutcome(false, (int)swFileSaveError_e.swGenericSaveError, 0);
        bool saved = _doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), data, null, ref errors, ref warnings);
        return new SaveOutcome(saved, errors, warnings);
    }
}
