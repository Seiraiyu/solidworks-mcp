using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The drawings suite's make-a-drawing scenario (design 611, Testing; plan 5.E): a block with a hole and a pin, assembled
/// and exploded, drawn on two sheets (three views and a section with imported and reference dimensions, a datum and a
/// position tolerance; the assembly exploded with a bill of materials and balloons), saved and exported as PDF, made only
/// through the MCP server's tools, played from Scenarios/drawing.transcript.json as an agent would make the calls. The
/// calls and their answers are kept in the run's drawing-transcript.jsonl.
/// </summary>
public sealed partial class DrawingsSuite
{
    [SkippableFact, Step(40)]
    public Task An_agent_makes_a_drawing_and_exports_its_pdf_through_MCP_tools_alone() => _run.CaseAsync(40, "make-a-drawing: the drawing transcript through the MCP server's tools (a 60 x 40 x 20 block with a 10 mm hole, a pin, the two assembled and exploded; Sheet1 with front, top, right and section A, the block's dimensions imported, width 60.000 and diameter 10.000 as reference dimensions, datum A and position Ø0.1 to A; Assembly with the exploded view, a two-row bill and two balloons), saved, and a PDF of a page per sheet, A3 each", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string part = New("make-block", ".SLDPRT"), pin = New("make-pin", ".SLDPRT"), asm = New("make-pinblock", ".SLDASM"), drawing = New("make", ".SLDDRW");
        string pdf = _run.Guard.NewScratchPath("make", ".pdf");
        string log = Path.Combine(_run.Directory, "drawing-transcript.jsonl");
        var transcript = Transcript.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios", "drawing.transcript.json"));
        var variables = new Dictionary<string, object?> { ["part"] = part, ["pin"] = pin, ["asm"] = asm, ["drawing"] = drawing, ["pdf"] = pdf };

        IReadOnlyList<TranscriptCall> calls;
        await using (var mcp = await McpSession.StartAsync(Sw))
        {
            try
            {
                calls = await transcript.RunAsync(mcp.CallAsync, variables, _run.Say);
            }
            catch (TranscriptFailedException e)
            {
                Transcript.Save(log, e.Calls);
                throw;
            }
        }
        Transcript.Save(log, calls);
        double Number(string name) => Convert.ToDouble(variables[name], CultureInfo.InvariantCulture);

        Assert.True(Math.Abs(Number("width") - 60) <= 0.001, $"the width dimension reads {variables["width"]}");
        Assert.True(Math.Abs(Number("diameter") - 10) <= 0.001, $"the diameter dimension reads {variables["diameter"]}");
        Assert.True(Math.Abs(Number("hatched_area") - 1000) <= Tol, $"section A hatches {variables["hatched_area"]} mm², not (60 - 10) x 20");
        Assert.NotEmpty((List<object?>)variables["imported"]!);
        var rows = (List<object?>)variables["bom_rows"]!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, ((List<object?>)variables["balloons"]!).Count);

        var read = await Call<DrawingReadRequest, DrawingReadResult>(new DrawingReadRequest { Document = drawing });
        Assert.Equal(new[] { "Sheet1", "Assembly" }, read.Sheets.Select(s => s.Name));
        var sheet1 = read.Sheets[0].Views;
        Assert.Equal(4, sheet1.Count);
        Assert.Contains(sheet1, v => v.Type == ViewType.Section && v.Name == (string)variables["section"]!);
        Assert.Equal(2, sheet1.Count(v => v.Type == ViewType.Projected));
        Assert.True(Assert.Single(read.Sheets[1].Views).Exploded, "the assembly view is not exploded");
        Assert.False(read.Document.Dirty && !string.Equals(variables["pdf_marked_dirty"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase), "the drawing has unsaved changes the export did not report");
        var properties = await Call<DrawingPropertiesRequest, DrawingPropertiesResult>(new DrawingPropertiesRequest { Document = drawing });
        Assert.Equal("SWMCP-0002", properties.Properties.Single(p => p.Name == "PartNo").Resolved);

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(pdf).Take(5).ToArray()));
        var pages = ((List<object?>)variables["pdf_pages"]!).Cast<JsonObject>().ToList();
        Assert.Equal(2, pages.Count);
        Assert.All(pages, p =>
        {
            double w = Convert.ToDouble(p["width_mm"], CultureInfo.InvariantCulture), h = Convert.ToDouble(p["height_mm"], CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(Math.Max(w, h) - 420) <= 0.1 && Math.Abs(Math.Min(w, h) - 297) <= 0.1, $"a page of {w} x {h}, not A3");
        });

        return FormattableString.Invariant($"{calls.Count} tool calls, longest {calls.Max(c => c.Ms)} ms ({calls.OrderByDescending(c => c.Ms).First().Tool}); ") +
               $"Sheet1 {string.Join(", ", sheet1.Select(v => $"{v.Name} {WireNames.ForEnum(v.Type)} {v.Extents}"))}; section {variables["section_label"]} hatching {Number("hatched_area")} mm²; " +
               $"imported {((List<object?>)variables["imported"]!).Count} dimensions; width {Number("width")}, diameter {Number("diameter")}; datum {variables["datum"]}, tolerance {variables["gtol"]}; " +
               $"bill {rows.Count} rows, balloons {((List<object?>)variables["balloons"]!).Count}; PDF {new FileInfo(pdf).Length} bytes, pages {string.Join(", ", pages.Select(p => $"{p["sheet"]} {p["width_mm"]} x {p["height_mm"]}"))}, marked_dirty {variables["pdf_marked_dirty"]}; transcript in {Path.GetFileName(log)}";
    });
}
