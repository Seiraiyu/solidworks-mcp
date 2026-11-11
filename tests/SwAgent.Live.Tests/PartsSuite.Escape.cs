using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The parts suite's 3.E cases (design 371, Testing): export (each format written and its header checked, the
/// document unchanged, the export preferences back as they were) and the escape hatch (a read that matches
/// model.mass in SI units, a model change with its rebuild status, a refused member). Every file is written in the
/// run's scratch directory under live-scratch, and nothing else.
/// </summary>
public sealed partial class PartsSuite
{
    /// <summary>A file in the run's scratch directory, which is under live-scratch.</summary>
    string ScratchFile(string name)
    {
        string path = Path.Combine(_run.Guard.ScratchDirectory, name);
        Assert.StartsWith(Path.GetFullPath(LiveScratch.Root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        return path;
    }

    Task<ApiCallResult> Api(ApiCallRequest request) => Call<ApiCallRequest, ApiCallResult>(request);

    async Task<long> IntegerPreference(int id) =>
        (long)(await Api(new ApiCallRequest { Target = "app", Member = "GetUserPreferenceIntegerValue", Args = new List<object?> { (long)id } })).Result!;

    async Task<bool> TogglePreference(int id) =>
        (bool)(await Api(new ApiCallRequest { Target = "app", Member = "GetUserPreferenceToggle", Args = new List<object?> { (long)id } })).Result!;

    static string FirstLine(string path)
    {
        using var reader = new StreamReader(path, Encoding.ASCII);
        return reader.ReadLine() ?? "";
    }

    [SkippableFact, Step(31)]
    public Task Exports_write_each_format_and_leave_the_part_and_preferences_as_they_were() => _run.CaseAsync(31, "export.part: STEP AP214 (AP242 tried, report only), Parasolid, STL with the user's own resolution and coarse and fine (or refused over custom), 3MF, of a drilled block; part and preferences unchanged", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("export");
        string path = block.Path!;
        // AP242 is report-only (3.E live run: SaveAs3 with swStepAP = 242 wrote AP203, PublishSTEP242File answered
        // InvalidPath, and this seat's UI offers only AP203 and AP214 with no Publish STEP 242). It is tried here on the
        // saved, unchanged block and again below on the drilled, unsaved one; each outcome goes into the case's line.
        var ap242Notes = new List<string>();
        async Task TryAp242(string name, string state)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var r = await Call<ExportPartRequest, ExportPartResult>(new ExportPartRequest { Document = path, Path = ScratchFile(name), Format = ExportFormat.Step, Options = new ExportOptions { StepSchema = StepSchema.Ap242 } });
                Assert.Contains("AP242", r.StepSchema);
                Assert.Equal(path, r.Document.Path, ignoreCase: true);
                ap242Notes.Add($"AP242 on the {state} part: WRITTEN, {r.Bytes} bytes in {clock.ElapsedMilliseconds} ms, {r.StepSchema}; {r.Checks.FirstOrDefault()}");
            }
            catch (SwAgentException e)
            {
                ap242Notes.Add($"AP242 on the {state} part: not written ({WireNames.ForEnum(e.Code)}: {e.Message})");
            }
        }
        await TryAp242("export-242-clean.step", "saved, unchanged");
        await Through(path, 20, 10, 5);
        double volume = await VolumeOf(path);
        Near(8000 - Math.PI * 25 * 10, volume, "the drilled block's volume");
        var before = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.True(before.Dirty, "the hole was cut after the block was saved, so the part has unsaved changes");
        long stepAp = await IntegerPreference(75);
        long stlQuality = await IntegerPreference(78);
        long stlUnits = await IntegerPreference(211);
        bool stlBinary = await TogglePreference(69);
        bool stlInfo = await TogglePreference(70);

        var notes = new List<string>();
        var times = new List<long>();
        async Task<ExportPartResult> Export(string name, ExportFormat format, ExportOptions? options = null, bool overwrite = false)
        {
            var clock = Stopwatch.StartNew();
            var result = await Call<ExportPartRequest, ExportPartResult>(new ExportPartRequest { Document = path, Path = ScratchFile(name), Format = format, Options = options, Overwrite = overwrite });
            times.Add(clock.ElapsedMilliseconds);
            Assert.True(File.Exists(result.Path) && new FileInfo(result.Path).Length == result.Bytes, $"{name} is on disk at the size reported");
            Assert.Equal(path, result.Document.Path, ignoreCase: true);
            Assert.True(result.Document.Dirty, $"{name}: the part kept its unsaved changes");
            notes.Add($"{name} {result.Bytes} bytes in {clock.ElapsedMilliseconds} ms{(result.Triangles == null ? "" : $", {result.Triangles} triangles")}{(result.StepSchema == null ? "" : $", {result.StepSchema}")}");
            return result;
        }

        var ap214 = await Export("export-214.step", ExportFormat.Step);
        Assert.Equal("ISO-10303-21;", FirstLine(ap214.Path));
        Assert.Contains("AUTOMOTIVE_DESIGN", ap214.StepSchema);
        await TryAp242("export-242.step", "drilled, unsaved");
        Assert.True((await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)).Dirty, "the part kept its unsaved changes through the AP242 attempt");
        var parasolid = await Export("export.x_t", ExportFormat.Parasolid);
        Assert.StartsWith("**ABCDEFGHIJKLMNOPQRSTUVWXYZ", FirstLine(parasolid.Path));
        // The user's own mesh settings, untouched (3.E live run, round 3: SOLIDWORKS does not take a custom resolution
        // back through its API, so resolution is only ever set over coarse or fine, and custom is not offered).
        await Export("export-own.stl", ExportFormat.Stl);
        if (stlQuality == 3)
        {
            var overCustom = await Refused(() => Call<ExportPartRequest, ExportPartResult>(new ExportPartRequest { Document = path, Path = ScratchFile("export-coarse.stl"), Format = ExportFormat.Stl, Options = new ExportOptions { Resolution = MeshResolution.Coarse } }));
            Assert.Equal("options.resolution", overCustom.Error.Details!["field"]);
            Assert.False(File.Exists(ScratchFile("export-coarse.stl")), "nothing was exported over the user's custom resolution");
            notes.Add("coarse refused over the user's custom resolution (invalid_argument, options.resolution), nothing set or written");
        }
        else
        {
            var coarse = await Export("export-coarse.stl", ExportFormat.Stl, new ExportOptions { Resolution = MeshResolution.Coarse });
            var fine = await Export("export-fine.stl", ExportFormat.Stl, new ExportOptions { Resolution = MeshResolution.Fine });
            Assert.True(coarse.Triangles <= fine.Triangles, $"the fine mesh ({fine.Triangles}) is no coarser than the coarse one ({coarse.Triangles})");
        }
        var threemf = await Export("export.3mf", ExportFormat.ThreeMf);
        Assert.Equal("zip", threemf.Header);

        var again = await Refused(() => Call<ExportPartRequest, ExportPartResult>(new ExportPartRequest { Document = path, Path = ap214.Path, Format = ExportFormat.Step }));
        Assert.Equal("path", again.Error.Details!["field"]);
        await Export("export-214.step", ExportFormat.Step, overwrite: true);

        Near(volume, await VolumeOf(path), "the volume after exporting");
        var after = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.True(after.Dirty);
        Assert.Equal(stepAp, await IntegerPreference(75));
        Assert.Equal(stlQuality, await IntegerPreference(78));
        Assert.Equal(stlUnits, await IntegerPreference(211));
        Assert.Equal(stlBinary, await TogglePreference(69));
        Assert.Equal(stlInfo, await TogglePreference(70));
        string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "export-preferences-journal.json");
        Assert.False(File.Exists(journal), "the export preferences' journal is gone: everything was put back");
        return $"{string.Join("; ", notes)}; overwrite refused without overwrite; the part kept its path and unsaved changes; " +
               $"swStepAP {stepAp}, swSTLQuality {stlQuality}, swExportStlUnits {stlUnits}, binary {stlBinary}, info {stlInfo} before and after; longest export {times.Max()} ms (export deadline 120 s); " +
               $"report only: {string.Join("; ", ap242Notes)}";
    });

    [SkippableFact, Step(32)]
    public Task The_escape_hatch_reads_changes_and_refuses() => _run.CaseAsync(32, "api.call: GetMassProperties2 matches model.mass in SI units; SetSuppression2 on the hole with its rebuild status; IFace2.GetFeature returns a reference; CloseDoc refused", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("escape");
        string path = block.Path!;
        string hole = await Through(path, 20, 10, 5);
        var times = new List<long>();
        async Task<ApiCallResult> Timed(ApiCallRequest r)
        {
            var clock = Stopwatch.StartNew();
            var result = await Api(r);
            times.Add(clock.ElapsedMilliseconds);
            return result;
        }

        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = path });
        var read = await Timed(new ApiCallRequest { Document = path, Target = "document", Member = "Extension.GetMassProperties2", Args = new List<object?> { 1L, 0L, false }, Rebuild = false });
        var values = ((List<object?>)read.Result!).Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(0L, read.Out["Status"]);
        Near(mass.VolumeMm3, values[3] * 1e9, "GetMassProperties2's volume, m³ × 10⁹");
        Near(mass.AreaMm2, values[4] * 1e6, "GetMassProperties2's area, m² × 10⁶");
        Near(mass.MassG, values[5] * 1000, "GetMassProperties2's mass, kg × 1000");
        Near(mass.CenterOfMass!.X, values[0] * 1000, "GetMassProperties2's centre X, m × 1000");

        string feature = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Single(f => f.Name == hole).Reference!;
        var suppress = await Timed(new ApiCallRequest { Document = path, Target = feature, Member = "SetSuppression2", Args = new List<object?> { 0L, 1L, null } });
        Assert.Equal(true, suppress.Result);
        Assert.NotNull(suppress.Rebuild);
        Assert.Equal(0, suppress.Rebuild!.Errors);
        Near(8000, await VolumeOf(path), "the block with its hole suppressed through api.call");
        await Timed(new ApiCallRequest { Document = path, Target = feature, Member = "SetSuppression2", Args = new List<object?> { 1L, 1L, null } });
        Near(8000 - Math.PI * 25 * 10, await VolumeOf(path), "the hole back after unsuppressing it through api.call");

        var top = await At(path, TopologyKind.Face, new Point3(5, 5, 10));
        var owner = await Timed(new ApiCallRequest { Document = path, Target = top.Reference!, Member = "GetFeature", Rebuild = false });
        var returned = (JsonObject)owner.Result!;
        Assert.StartsWith("r1.feature.", (string)returned["ref"]!);

        var refused = await Refused(() => Api(new ApiCallRequest { Target = "app", Member = "CloseDoc", Args = new List<object?> { path } }));
        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        Assert.Equal("CloseDoc", refused.Error.Details!["refused"]);
        Assert.Contains((await _run.AddinStateAsync()).Documents, d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "logs", "api-calls.log");
        var lines = File.ReadAllLines(log).Reverse().Take(20).ToList();
        Assert.Contains(lines, l => l.Contains("\"member\":\"CloseDoc\"") && l.Contains("\"outcome\":\"invalid_argument\""));
        Assert.Contains(lines, l => l.Contains("\"member\":\"Extension.GetMassProperties2\"") && l.Contains("\"outcome\":\"ok\""));
        return $"GetMassProperties2: volume {values[3]} m³, area {values[4]} m², mass {values[5]} kg, status 0, matching model.mass; interfaces {string.Join(" > ", read.Interfaces)}; " +
               $"SetSuppression2 on {hole}: {suppress.Result}, rebuild errors {suppress.Rebuild.Errors}, volume 8000, then the hole back with SetSuppression2(1, 1, null); " +
               $"IFace2.GetFeature of the top face: {Json.Write(returned)}; CloseDoc refused ({refused.Error.Details["reason"]}), the part still open; both logged in api-calls.log; longest call {times.Max()} ms (api deadline 30 s)";
    });
}
