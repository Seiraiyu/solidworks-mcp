using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The sheet-metal suite's build-an-enclosure scenario (design 454, Testing; plan 6.D): a bracket with a flange, a closed
/// hem and a relief hole cut across the flange's bend, made only through the MCP server's tools, played from
/// Scenarios/enclosure.transcript.json as an agent would make the calls, and ending with its flat size and a DXF. The
/// hole sits where the flat bend meets the base, the side that stays put: SOLIDWORKS 2026 SP4 makes no Fold once a cut
/// reaches into the side that turns (6.C live run). Teardrop and rolled hems are refused, so the hem is closed. The calls
/// and their answers are kept in the run's enclosure-transcript.jsonl.
/// </summary>
public sealed partial class SheetMetalSuite
{
    [SkippableFact, Step(23)]
    public Task An_agent_builds_an_enclosure_bracket_and_exports_its_flat_pattern_through_MCP_tools_alone() => _run.CaseAsync(23, "build-an-enclosure: the enclosure transcript through the MCP server's tools (a 100 × 60 blank, a 25 mm flange, a 10 mm closed hem, a 4 mm hole cut across the unfolded flange bend and folded back), its flat 100 × (60 + 23.2832 + the hem's share, measured on a tab with that hem alone), and a DXF of that size", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string part = NewPart("enclosure");
        string dxf = ScratchFile("enclosure.dxf");
        string log = Path.Combine(_run.Directory, "enclosure-transcript.jsonl");
        var transcript = Transcript.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios", "enclosure.transcript.json"));
        var variables = new Dictionary<string, object?> { ["part"] = part, ["dxf"] = dxf };

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

        // The closed hem's share of the flat, from a tab with the same hem on the same edge and nothing else.
        var (alone, _) = await Sheet("enclosure-hem-alone");
        await Call<SheetMetalHemRequest, SheetMetalFeatureResult>(new SheetMetalHemRequest { Document = alone, Edges = new List<string> { await EdgeAt(alone, 50, 0) }, Kind = HemKind.Closed, LengthMm = 10 });
        var hemFlat = (await Read(alone)).Flat!;
        Flat(100, hemFlat.LengthMm, "the hem tab's flat length");
        double hem = hemFlat.WidthMm - 60;

        var read = await Read(part);
        Assert.Equal(SheetState.Folded, read.State);
        Assert.Equal(2, read.Bends.Count);
        Assert.NotNull(read.Flat);
        Flat(100, read.Flat!.LengthMm, "the bracket's flat length");
        // Two cut-list values, each rounded to the part's display precision: the hem's share and the bracket's width.
        double width = 60 + Across25 + hem;
        Within(width, read.Flat.WidthMm, 0.011, "the bracket's flat width: the blank's 60, the flange's 23.2832 and the hem's share");
        Near(read.Flat.LengthMm, Number("flat_length"), "the flat length sw_sheetmetal_read gave the agent");
        Near(read.Flat.WidthMm, Number("flat_width"), "the flat width sw_sheetmetal_read gave the agent");

        Within(read.Flat.LengthMm, Number("dxf_length"), 0.015, "the DXF's length against the flat size");
        Within(read.Flat.WidthMm, Number("dxf_width"), 0.015, "the DXF's width against the flat size");
        Assert.True(Number("dxf_bend_lines") >= 2, $"bend lines for the flange's bend and the hem's: {variables["dxf_bend_lines"]}");
        // The header is a note, not a check: ExportFlatPatternView writes the drawing sheet there (6.D live run).
        var sides = HeaderSides(dxf);
        bool headerIsFlat = sides != null && Math.Abs(Number("dxf_length") - sides.Value.Long) <= 1e-6 && Math.Abs(Number("dxf_width") - sides.Value.Short) <= 1e-6;

        var box = await BoxOf(part);
        Near(100, box.Max.X - box.Min.X, "the bracket's length", 1e-5);
        var holes = (await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = part, Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder })).Entities
            .Where(f => Math.Abs((f.RadiusMm ?? 0) - 2) < 1e-6).ToList();
        Assert.NotEmpty(holes);
        var state = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, part, StringComparison.OrdinalIgnoreCase));
        // The transcript saved the part before exporting it; ExportToDWG2 may mark it changed (marked_dirty, 6.D live run).
        bool marked = string.Equals(variables["dxf_marked_dirty"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(marked, state.Dirty);

        return FormattableString.Invariant($"{calls.Count} tool calls ({string.Join(", ", calls.Select(c => c.Tool))}), longest {calls.Max(c => c.Ms)} ms; ") +
               $"{variables["base"]}, {variables["flange"]} ({variables["flange_bend"]}), {variables["hem"]}, {variables["unfold"]}, {variables["cut"]}, {variables["fold"]}; " +
               FormattableString.Invariant($"flat {read.Flat.LengthMm} × {read.Flat.WidthMm} (expected 100 × {width:0.####}, the hem's share {hem:0.####} from a tab with it alone, {hemFlat.LengthMm} × {hemFlat.WidthMm}); ") +
               FormattableString.Invariant($"DXF by {variables["dxf_method"]} (marked_dirty {marked}):{Number("dxf_length"):0.####} × {Number("dxf_width"):0.####}, {variables["dxf_bend_lines"]} bend lines, {new FileInfo(dxf).Length} bytes, {(sides == null ? "no $EXTMIN/$EXTMAX" : headerIsFlat ? "its header the flat" : $"its header {sides.Value.Long:0.####} × {sides.Value.Short:0.####}, not the flat")}; ") +
               FormattableString.Invariant($"box {box.Max.X - box.Min.X:0.####} × {box.Max.Y - box.Min.Y:0.####} × {box.Max.Z - box.Min.Z:0.####}; {holes.Count} faces of the 4 mm hole; transcript in {Path.GetFileName(log)}");
    });
}
