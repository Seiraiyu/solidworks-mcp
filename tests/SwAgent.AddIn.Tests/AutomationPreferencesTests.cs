using System.Linq;
using SolidWorks.Interop.swconst;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AutomationPreferencesTests
{
    [Fact]
    public void Are_the_roadmaps_three_the_over_defining_dimension_prompt_and_the_assembly_ones()
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
            },
            AutomationPreferences.All.Select(p => (p.Id, p.AutomationValue)).ToArray());
    }
}
