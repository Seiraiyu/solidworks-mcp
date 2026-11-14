using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>
/// The preferences automation needs (roadmap: no dimension box on creation, no save reminder, no close-sketch prompt;
/// plan 3.B: no prompt when a dimension would over-define a sketch, and such a dimension stays driving, so sketch.create
/// finds the sketch over defined and rolls it back instead of waiting on a dialog; design 455, Session preferences: no
/// Begin Assembly command on a new assembly, components loaded resolved rather than lightweight, Large Assembly Mode and
/// Large Design Review not entered on their own; design 611, Session preferences: no Model View command on a new drawing, no
/// sheet-format dialog on a new sheet, nothing inserted automatically when a view is made (centre marks, centre lines,
/// balloons, dimensions marked for drawing), no automatic scaling of new views).
/// </summary>
public static class AutomationPreferences
{
    public static readonly IReadOnlyList<AutomationPreference> All = new[]
    {
        new AutomationPreference("input_dim_val_on_create", (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false),
        new AutomationPreference("save_reminder_enable", (int)swUserPreferenceToggle_e.swSaveReminderEnable, false),
        new AutomationPreference("sketch_prompt_to_close_sketch", (int)swUserPreferenceToggle_e.swSketchPromptToCloseSketch, false),
        new AutomationPreference("sketch_overdefining_dims_prompt", (int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false),
        new AutomationPreference("sketch_overdefining_dims_set_driven", (int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault, false),
        new AutomationPreference("insert_component_for_new_assembly", (int)swUserPreferenceToggle_e.swInsertComponentForNewAssembly, false),
        new AutomationPreference("auto_load_parts_lightweight", (int)swUserPreferenceToggle_e.swAutoLoadPartsLightweight, false),
        new AutomationPreference("always_resolve_subassemblies", (int)swUserPreferenceToggle_e.swPerformanceAlwaysResolveSubassemblies, true),
        new AutomationPreference("large_asm_mode_enabled", (int)swUserPreferenceToggle_e.swLargeAsmModeEnabled, false),
        new AutomationPreference("large_asm_mode_auto_load_lightweight", (int)swUserPreferenceToggle_e.swLargeAsmModeAutoLoadLightweight, false),
        new AutomationPreference("large_asm_mode_use_large_design_review", (int)swUserPreferenceToggle_e.swLargeAsmModeUseLargeDesignReview, false),
        new AutomationPreference("insert_view_for_new_drawing", (int)swUserPreferenceToggle_e.swInsertViewForNewDrawing, false),
        new AutomationPreference("drawing_show_sheet_format_dialog", (int)swUserPreferenceToggle_e.swDrawingShowSheetFormatDialog, false),
        new AutomationPreference("detailing_auto_insert_center_marks_for_holes", (int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForHoles, false),
        new AutomationPreference("detailing_auto_insert_center_marks_for_fillets", (int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForFillets, false),
        new AutomationPreference("detailing_auto_insert_center_marks_for_slots", (int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForSlots, false),
        new AutomationPreference("detailing_auto_insert_center_lines", (int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterLines, false),
        new AutomationPreference("detailing_auto_insert_balloons", (int)swUserPreferenceToggle_e.swDetailingAutoInsertBalloons, false),
        new AutomationPreference("detailing_auto_insert_dims_marked_for_drawing", (int)swUserPreferenceToggle_e.swDetailingAutoInsertDimsMarkedForDrawing, false),
        new AutomationPreference("automatic_scaling_3_view_drawings", (int)swUserPreferenceToggle_e.swAutomaticScaling3ViewDrawings, false),
    };
}

/// <summary>SOLIDWORKS' toggle preferences, through the ISldWorks the add-in was given. UI thread only.</summary>
internal sealed class SolidWorksPreferenceStore : IPreferenceStore
{
    readonly ISldWorks _sw;

    public SolidWorksPreferenceStore(ISldWorks sw)
    {
        _sw = sw;
    }

    public bool Get(int id) => _sw.GetUserPreferenceToggle(id);

    public void Set(int id, bool value) => _sw.SetUserPreferenceToggle(id, value);
}
