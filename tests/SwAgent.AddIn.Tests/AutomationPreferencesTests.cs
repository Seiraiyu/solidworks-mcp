using System.Linq;
using SolidWorks.Interop.swconst;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AutomationPreferencesTests
{
    [Fact]
    public void Are_the_three_the_roadmap_names_all_turned_off()
    {
        Assert.Equal(
            new[]
            {
                ((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false),
                ((int)swUserPreferenceToggle_e.swSaveReminderEnable, false),
                ((int)swUserPreferenceToggle_e.swSketchPromptToCloseSketch, false),
            },
            AutomationPreferences.All.Select(p => (p.Id, p.AutomationValue)).ToArray());
    }
}
