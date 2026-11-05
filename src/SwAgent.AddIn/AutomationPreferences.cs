using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn;

/// <summary>
/// The preferences automation needs (roadmap: no dimension box on creation, no save reminder, no close-sketch prompt;
/// plan 3.B: no prompt when a dimension would over-define a sketch, and such a dimension stays driving, so sketch.create
/// finds the sketch over defined and rolls it back instead of waiting on a dialog).
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
