using System;
using SwAgent.Client;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class SolidWorksAttachTests
{
    [Fact]
    public void A_process_without_a_SOLIDWORKS_window_has_no_main_window_and_is_not_ready()
    {
        Assert.Equal(IntPtr.Zero, ComSolidWorksAttach.MainWindow(Environment.ProcessId));
        var readiness = ComSolidWorksAttach.Instance.Probe(Environment.ProcessId);
        Assert.False(readiness.Ready);
        Assert.Contains("no SOLIDWORKS main window", readiness.Reason);
        Assert.False(ComSolidWorksAttach.Instance.IsHung(Environment.ProcessId));
    }
}
