using System.Linq;
using SolidWorks.Interop.swconst;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AutomationPreferencesTests
{
    [Fact]
    public void Are_the_roadmaps_three_and_the_over_defining_dimension_prompt_all_turned_off()
    {
        Assert.Equal(
            new[]
            {
                ((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false),
                ((int)swUserPreferenceToggle_e.swSaveReminderEnable, false),
                ((int)swUserPreferenceToggle_e.swSketchPromptToCloseSketch, false),
                ((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false),
                ((int)swUserPreferenceToggle_e.swSketchOverdefiningDimsSetDrivenByDefault, false),
            },
            AutomationPreferences.All.Select(p => (p.Id, p.AutomationValue)).ToArray());
    }
}
