using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The parts suite's build-a-part scenario (design 371, Testing; plan 3.F): an L-bracket made only through the MCP
/// server's tools, played from Scenarios/bracket.transcript.json as an agent would make the calls, and then checked
/// against its exact mass properties, its box, its holes and its STEP copy. The calls and their answers are kept in
/// the run's bracket-transcript.jsonl.
/// </summary>
public sealed partial class PartsSuite
{
    /// <summary>The L profile (60 x 6 base, 6 x 44 upright) 40 mm deep, the 5 mm fillet's spandrel, less two Ø5 holes through 6 mm.</summary>
    static readonly double BracketVolume = (60 * 6 + 6 * 44) * 40 + 5 * 5 * (1 - Math.PI / 4) * 40 - 2 * Math.PI * 2.5 * 2.5 * 6;

    /// <summary>The bracket's centre of mass in x and y, from its pieces; z is 0 by symmetry.</summary>
    static (double X, double Y) BracketCentre()
    {
        double r = 5, quarter = Math.PI * r * r / 4, spandrel = r * r - quarter, d = 4 * r / (3 * Math.PI);
        double s = (r * r * (6 + r / 2) - quarter * (6 + r - d)) / spandrel; // the spandrel's centroid, the same in x and y
        var pieces = new[]
        {
            (V: 60.0 * 6 * 40, X: 30.0, Y: 3.0),
            (V: 6.0 * 44 * 40, X: 3.0, Y: 28.0),
            (V: spandrel * 40, X: s, Y: s),
            (V: -2 * Math.PI * 2.5 * 2.5 * 6, X: 40.0, Y: 3.0),
        };
        double v = pieces.Sum(p => p.V);
        return (pieces.Sum(p => p.V * p.X) / v, pieces.Sum(p => p.V * p.Y) / v);
    }

    [SkippableFact, Step(33)]
    public Task An_agent_builds_a_bracket_through_MCP_tools_alone() => _run.CaseAsync(33, "build-a-part: the bracket transcript through the MCP server's tools; exact mass properties, box, holes and a STEP copy", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string part = NewPart("bracket");
        string step = ScratchFile("bracket.step");
        string log = Path.Combine(_run.Directory, "bracket-transcript.jsonl");
        var transcript = Transcript.Load(Path.Combine(AppContext.BaseDirectory, "Scenarios", "bracket.transcript.json"));
        var variables = new Dictionary<string, object?> { ["part"] = part, ["step"] = step };

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

        Assert.Equal("fully_defined", variables["solve"]);
        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = part });
        Assert.Equal(1, mass.Bodies);
        Near(BracketVolume, mass.VolumeMm3, "the bracket's volume");
        Near(BracketVolume, Convert.ToDouble(variables["volume"], System.Globalization.CultureInfo.InvariantCulture), "the volume sw_model_mass gave the agent");
        var (cx, cy) = BracketCentre();
        Assert.True(Math.Abs(mass.CenterOfMass!.X - cx) < 1e-4 && Math.Abs(mass.CenterOfMass.Y - cy) < 1e-4 && Math.Abs(mass.CenterOfMass.Z) < 1e-4,
            $"the centre of mass: expected ({cx:0.######}, {cy:0.######}, 0), got {mass.CenterOfMass}");

        var box = await BoxOf(part);
        Near(0, box.Min.X, "box min x"); Near(0, box.Min.Y, "box min y"); Near(-20, box.Min.Z, "box min z");
        Near(60, box.Max.X, "box max x"); Near(50, box.Max.Y, "box max y"); Near(20, box.Max.Z, "box max z");

        var cylinders = (await Find(part, new ModelFindRequest { Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder })).Entities;
        Assert.Equal(2, cylinders.Count(c => Math.Abs(c.RadiusMm!.Value - 2.5) < 1e-6));
        Assert.Equal(1, cylinders.Count(c => Math.Abs(c.RadiusMm!.Value - 5) < 1e-6));

        Assert.True(File.Exists(step) && new FileInfo(step).Length > 0, "the STEP copy is on disk");
        Assert.Equal("ISO-10303-21;", FirstLine(step));
        var state = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, part, StringComparison.OrdinalIgnoreCase));
        Assert.False(state.Dirty, "the transcript saved the part last");

        return $"{calls.Count} tool calls ({string.Join(", ", calls.Select(c => c.Tool))}), longest {calls.Max(c => c.Ms)} ms; " +
               $"{variables["body"]}, {variables["fillet"]}, {variables["holes"]}; volume {mass.VolumeMm3:0.######} mm³ (expected {BracketVolume:0.######}), " +
               $"centre ({mass.CenterOfMass.X:0.######}, {mass.CenterOfMass.Y:0.######}, {mass.CenterOfMass.Z:0.######}), box {box.Min} to {box.Max}; " +
               $"cylinders {cylinders.Count}; STEP {new FileInfo(step).Length} bytes; transcript in {Path.GetFileName(log)}";
    });
}
