using System;
using Xunit;

namespace SwAgent.Live.Tests;

public class LiveReportTests
{
    static ReportHeader Header(string? stopped = null) => new ReportHeader
    {
        RunId = "20261105-140000",
        Suite = "parts suite",
        Machine = "seiraiyu2",
        SwRevision = "34.4.0",
        AddinVersion = "0.1.0+abc",
        ClientVersion = "0.1.0+abc",
        Commit = "abc1234",
        Started = new DateTime(2026, 11, 5, 14, 0, 0),
        Finished = new DateTime(2026, 11, 5, 14, 20, 0),
        RunDirectory = @"C:\runs\20261105-140000",
        Stopped = stopped,
    };

    [Fact]
    public void Lists_every_case_in_order_with_its_outcome_and_evidence()
    {
        string report = LiveReport.Render(Header(), new[]
        {
            new CaseResult(1, "auto-load", CaseOutcome.Pass, "pipe up after 1710 ms", 3.2),
            new CaseResult(2, "kill | crash", CaseOutcome.Fail, "sw_crashed after 1400 ms", 5),
            new CaseResult(3, "close", CaseOutcome.Skip, "guard: Bracket.SLDPRT is open", 0.1),
        });
        Assert.Contains("SOLIDWORKS revision: 34.4.0", report);
        Assert.Contains("- Commit: abc1234", report);
        Assert.Contains("| 1 | auto-load | PASS | pipe up after 1710 ms | 3.2 |", report);
        Assert.Contains(@"| 2 | kill \| crash | FAIL |", report);
        Assert.Contains("| 3 | close | SKIP | guard: Bracket.SLDPRT is open |", report);
        Assert.Contains("1 passed, 1 failed, 1 skipped", report);
        Assert.True(report.IndexOf("auto-load", StringComparison.Ordinal) < report.IndexOf("close |", StringComparison.Ordinal));
        Assert.DoesNotContain("Stopped", report);
    }

    [Fact]
    public void Says_why_a_run_stopped()
    {
        string report = LiveReport.Render(Header("documents the suite did not create are open: Bracket"), Array.Empty<CaseResult>());
        Assert.Contains("**Stopped:** documents the suite did not create are open: Bracket", report);
        Assert.Contains("0 passed, 0 failed, 0 skipped", report);
    }

    [Fact]
    public void Evidence_on_several_lines_stays_in_its_row()
    {
        string report = LiveReport.Render(Header(), new[] { new CaseResult(1, "x", CaseOutcome.Pass, "a\r\nb", 1) });
        Assert.Contains("| 1 | x | PASS | a<br>b | 1.0 |", report);
    }

    [Fact]
    public void The_title_names_the_suite()
    {
        string report = LiveReport.Render(Header(), Array.Empty<CaseResult>());
        Assert.StartsWith("# swmcp live parts suite: run 20261105-140000", report);
    }
}
