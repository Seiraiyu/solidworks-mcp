using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SwAgent.Live.Tests;

public enum CaseOutcome
{
    Pass,
    Fail,
    Skip,
}

/// <summary>One live case's outcome and the evidence it saw.</summary>
public sealed record CaseResult(int Step, string Name, CaseOutcome Outcome, string Evidence, double Seconds);

/// <summary>What a run report says about the run as a whole.</summary>
public sealed class ReportHeader
{
    public string RunId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string? SwRevision { get; set; }
    public string? AddinVersion { get; set; }
    public string ClientVersion { get; set; } = "";

    /// <summary>The commit the client was built from (the +sha of its version), or null when unknown.</summary>
    public string? Commit { get; set; }

    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public string RunDirectory { get; set; } = "";

    /// <summary>Why the run stopped early, when it did.</summary>
    public string? Stopped { get; set; }
}

/// <summary>The run report attached to the pull request: Markdown, one row per case.</summary>
public static class LiveReport
{
    public static string Render(ReportHeader header, IReadOnlyList<CaseResult> results)
    {
        var s = new StringBuilder();
        s.AppendLine($"# swmcp live fault suite: run {header.RunId}");
        s.AppendLine();
        s.AppendLine($"- Machine: {header.Machine}");
        s.AppendLine($"- SOLIDWORKS revision: {header.SwRevision ?? "unknown"}");
        s.AppendLine($"- Add-in: {header.AddinVersion ?? "unknown"}");
        s.AppendLine($"- Client (swmcp): {header.ClientVersion}");
        s.AppendLine($"- Commit: {header.Commit ?? "unknown"}");
        s.AppendLine($"- Started {header.Started.ToString("u", CultureInfo.InvariantCulture)}, finished {header.Finished.ToString("u", CultureInfo.InvariantCulture)}");
        s.AppendLine($"- Logs: {header.RunDirectory}");
        if (header.Stopped != null) s.AppendLine($"- **Stopped:** {header.Stopped}");
        s.AppendLine();
        s.AppendLine($"{Count(results, CaseOutcome.Pass)} passed, {Count(results, CaseOutcome.Fail)} failed, {Count(results, CaseOutcome.Skip)} skipped");
        s.AppendLine();
        s.AppendLine("| Step | Case | Result | Evidence | Seconds |");
        s.AppendLine("|------|------|--------|----------|---------|");
        foreach (var r in results.OrderBy(r => r.Step))
        {
            s.AppendLine($"| {r.Step} | {Cell(r.Name)} | {r.Outcome.ToString().ToUpperInvariant()} | {Cell(r.Evidence)} | {r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} |");
        }
        return s.ToString();
    }

    static int Count(IReadOnlyList<CaseResult> results, CaseOutcome outcome) => results.Count(r => r.Outcome == outcome);

    static string Cell(string text) => text.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");
}
