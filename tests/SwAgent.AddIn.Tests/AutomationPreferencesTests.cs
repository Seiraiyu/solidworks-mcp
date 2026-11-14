using System.Linq;
using SolidWorks.Interop.swconst;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AutomationPreferencesTests
{
    [Fact]
    public void Are_the_roadmaps_three_the_over_defining_dimension_prompt_the_assembly_ones_and_the_drawing_ones()
    {
        Assert.Equal(
            new[]
            {
                ((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false),
                ((int)swUserPreferenceToggle_e.swSaveReminderEnable, false),
                ((int)swUserPreferenceToggle_e.swSketchPromptToCloseSketch, false),
                ((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false),
                ((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault, false),
                ((int)swUserPreferenceToggle_e.swInsertComponentForNewAssembly, false),
                ((int)swUserPreferenceToggle_e.swAutoLoadPartsLightweight, false),
                ((int)swUserPreferenceToggle_e.swPerformanceAlwaysResolveSubassemblies, true),
                ((int)swUserPreferenceToggle_e.swLargeAsmModeEnabled, false),
                ((int)swUserPreferenceToggle_e.swLargeAsmModeAutoLoadLightweight, false),
                ((int)swUserPreferenceToggle_e.swLargeAsmModeUseLargeDesignReview, false),
                ((int)swUserPreferenceToggle_e.swInsertViewForNewDrawing, false),
                ((int)swUserPreferenceToggle_e.swDrawingShowSheetFormatDialog, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForHoles, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForFillets, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterMarksForSlots, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertCenterLines, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertBalloons, false),
                ((int)swUserPreferenceToggle_e.swDetailingAutoInsertDimsMarkedForDrawing, false),
                ((int)swUserPreferenceToggle_e.swAutomaticScaling3ViewDrawings, false),
            },
            AutomationPreferences.All.Select(p => (p.Id, p.AutomationValue)).ToArray());
    }

    sealed class Store : SwAgent.AddIn.Core.IPreferenceStore
    {
        public readonly System.Collections.Generic.Dictionary<int, bool> Values = new System.Collections.Generic.Dictionary<int, bool>();
        public bool Get(int id) => Values.TryGetValue(id, out var v) ? v : true;
        public void Set(int id, bool value) => Values[id] = value;
    }

    static readonly string[] DrawingOnes =
    {
        "insert_view_for_new_drawing", "drawing_show_sheet_format_dialog", "detailing_auto_insert_center_marks_for_holes",
        "detailing_auto_insert_center_marks_for_fillets", "detailing_auto_insert_center_marks_for_slots", "detailing_auto_insert_center_lines",
        "detailing_auto_insert_balloons", "detailing_auto_insert_dims_marked_for_drawing", "automatic_scaling_3_view_drawings",
    };

    [Fact]
    public void The_drawing_ones_are_journalled_while_applied_and_put_back_after()
    {
        string journal = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".json");
        var store = new Store();
        var guard = new SwAgent.AddIn.Core.PreferenceGuard(store, AutomationPreferences.All, journal, SwAgent.AddIn.Core.NullLog.Instance);
        guard.Apply();
        var written = SwAgent.Contracts.Serialization.JsonCodec.Deserialize<SwAgent.AddIn.Core.PreferenceJournal>(System.IO.File.ReadAllText(journal))!;
        foreach (var name in DrawingOnes)
        {
            var entry = Assert.Single(written.Entries, e => e.Name == name);
            Assert.True(entry.Original);
            Assert.False(entry.Applied);
            Assert.False(store.Values[entry.Id]);
        }
        guard.Restore("test");
        Assert.False(System.IO.File.Exists(journal));
        Assert.All(DrawingOnes, name => Assert.True(store.Values[AutomationPreferences.All.Single(p => p.Name == name).Id]));
    }
}
