using Xunit;

namespace SwAgent.AddIn.Tests;

public class RebuildCountTests
{
    [Fact]
    public void Counts_errors_and_warnings_from_the_whats_wrong_flags()
    {
        Assert.Equal((2, 1), SolidWorksEventBridge.CountWhatsWrong(new object[] { false, true, false }));
        Assert.Equal((0, 0), SolidWorksEventBridge.CountWhatsWrong(null));
        Assert.Equal((1, 0), SolidWorksEventBridge.CountWhatsWrong(new[] { false }));
    }
}
