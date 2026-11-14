using System.Collections.Generic;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// What export.drawing sets in SOLIDWORKS' export preferences for the call, and puts back (design 611, Export), and the
/// extension each format is written with. PDF: no viewer opened after saving. DXF and DWG: the shown sheet only (the
/// operation shows each sheet in turn and names each file itself), at sheet scale rather than 1:1 model scale (so the file
/// is in sheet mm, as drawing.read's extents are), views written as plain entities rather than blocks (so they can be
/// measured), and no mapping-file or hidden-layer box to wait for the user.
/// </summary>
public static class DrawingExportSettings
{
    public static string Extension(DrawingExportFormat format) => format switch
    {
        DrawingExportFormat.Pdf => ".pdf",
        DrawingExportFormat.Dxf => ".dxf",
        _ => ".dwg",
    };

    public static IReadOnlyList<PreferenceSetting> For(DrawingExportFormat format)
    {
        if (format == DrawingExportFormat.Pdf) return new[] { Toggle(nameof(swUserPreferenceToggle_e.swPDFViewOnSave), (int)swUserPreferenceToggle_e.swPDFViewOnSave, false) };
        return new[]
        {
            Integer(nameof(swUserPreferenceIntegerValue_e.swDxfMultiSheetOption), (int)swUserPreferenceIntegerValue_e.swDxfMultiSheetOption, (int)swDxfMultisheet_e.swDxfActiveSheetOnly),
            Integer(nameof(swUserPreferenceIntegerValue_e.swDxfOutputNoScale), (int)swUserPreferenceIntegerValue_e.swDxfOutputNoScale, 0),
            Toggle(nameof(swUserPreferenceToggle_e.swDxfExportViewAsBlock), (int)swUserPreferenceToggle_e.swDxfExportViewAsBlock, false),
            Toggle(nameof(swUserPreferenceToggle_e.swDxfMapping), (int)swUserPreferenceToggle_e.swDxfMapping, false),
            Toggle(nameof(swUserPreferenceToggle_e.swDXFExportHiddenLayersWarnIsOn), (int)swUserPreferenceToggle_e.swDXFExportHiddenLayersWarnIsOn, false),
        };
    }

    static PreferenceSetting Integer(string name, int id, int value) => new PreferenceSetting(name, PreferenceKind.Integer, id, value);

    static PreferenceSetting Toggle(string name, int id, bool value) => new PreferenceSetting(name, PreferenceKind.Toggle, id, value ? 1 : 0);
}
