using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>The sheet-metal suite's run: its own id (…-sheet-metal-suite), directory, scratch directory and report.</summary>
public sealed class SheetMetalSuiteRun : LiveRun
{
    public SheetMetalSuiteRun()
        : base("sheet-metal-suite")
    {
    }
}

/// <summary>
/// The sheet-metal live suite (design 454, Testing; plan 6.A covers base flanges, reading a part back, changing its
/// parameters and the Flat-Pattern audit of phase 3's features): every case drives seiraiyu2's SOLIDWORKS through
/// SwAgent.Client exactly as swmcp serve does, in [Step] order, and checks geometry against exact values: the bend
/// arithmetic's, and CADapter's measured flat sizes. It never ends SOLIDWORKS. Before every case it stops the run if a
/// document it did not create is open; it creates documents only in its scratch directory and closes them all at the
/// end. Skipped unless SWMCP_LIVE=1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class SheetMetalSuite : IClassFixture<SheetMetalSuiteRun>
{
    const double Tight = 1e-6;

    /// <summary>A 90° bend's allowance at R 3, T 2 and K = 0.5: π/2 × (3 + 1).</summary>
    static readonly double HalfK = Math.PI / 2 * 4;

    readonly LiveRun _run;

    public SheetMetalSuite(SheetMetalSuiteRun run)
    {
        _run = run;
    }

    Supervisor Sw => _run.Supervisor;

    Task<TResult> Call<TRequest, TResult>(TRequest request)
        where TRequest : class
        where TResult : class =>
        Sw.CallAsync<TRequest, TResult>(request, TimeSpan.FromSeconds(120));

    /// <summary>A scratch path for a new part, recorded as this run's before anything creates it.</summary>
    string NewPart(string name)
    {
        string path = _run.Guard.NewScratchPath(name);
        _run.Guard.Created(path);
        return path;
    }

    static void Near(double expected, double actual, string what, double tolerance = Tight) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)), $"{what}: expected {expected}, got {actual}");

    /// <summary>Within the cut list's rounding: SOLIDWORKS writes the flat size to the part's display precision.</summary>
    static void Flat(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= 0.011, $"{what}: expected {expected:0.####} mm, the cut list gives {actual:0.####} mm");

    static async Task<SwAgentException> Refused(Func<Task> call) => await Assert.ThrowsAsync<SwAgentException>(call);

    Task<SheetMetalReadResult> Read(string path) => Call<SheetMetalReadRequest, SheetMetalReadResult>(new SheetMetalReadRequest { Document = path });

    async Task<double> VolumeOf(string document) => (await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = document })).VolumeMm3;

    async Task<Box3> BoxOf(string document) =>
        (await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = document, Precise = true })).Box!;

    /// <summary>A new part with a 100 × 60 mm rectangle on the front plane, made a 2 mm tab at R 3: the suite's sheet.</summary>
    async Task<(string Path, SheetMetalFeatureResult Flange)> Sheet(string name)
    {
        string path = NewPart(name);
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var outline = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "sheet", Corner1 = new Point2(0, 0), Corner2 = new Point2(100, 60) } },
        });
        var flange = await Call<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>(new SheetMetalBaseFlangeRequest { Document = path, Sketch = outline.Sketch.Name, ThicknessMm = 2, BendRadiusMm = 3 });
        return (path, flange);
    }

    /// <summary>A new part with a U of 30, 50 and 30 mm on the front plane, made a 2 mm channel 20 mm deep at R 3.</summary>
    async Task<(string Path, SheetMetalFeatureResult Flange)> Channel(string name)
    {
        string path = NewPart(name);
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var u = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Lines = new List<SketchLine>
            {
                new SketchLine { Id = "a", Start = new Point2(0, 0), End = new Point2(0, 30) },
                new SketchLine { Id = "b", Start = new Point2(0, 30), End = new Point2(50, 30) },
                new SketchLine { Id = "c", Start = new Point2(50, 30), End = new Point2(50, 0) },
            },
        });
        var flange = await Call<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>(new SheetMetalBaseFlangeRequest { Document = path, Sketch = u.Sketch.Name, ThicknessMm = 2, BendRadiusMm = 3, DepthMm = 20 });
        return (path, flange);
    }

    /// <summary>The channel's straight legs: 27 + 44 + 27 with the lines the inner face (material outside the U, the chain's left), 25 + 40 + 25 with them the outer face.</summary>
    static double ChannelLegs(SheetMetalFeatureResult flange) => flange.Checks.Any(c => c.Contains("material on the chain's left")) ? 98 : 90;

    [SkippableFact, Step(1)]
    public Task SOLIDWORKS_is_ready_with_nothing_of_the_owners_open() => _run.CaseAsync(1, "SOLIDWORKS ready, only the suite's documents open, diagnostics on", async () =>
    {
        await _run.PrepareSolidWorksAsync();
        var clock = Stopwatch.StartNew();
        SessionStatusResult status;
        while (true)
        {
            try
            {
                status = await Sw.CallAsync<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), TimeSpan.FromSeconds(60));
                break;
            }
            catch (SwAgentException e) when ((e.Code == ErrorCode.SwStarting || e.Code == ErrorCode.Busy) && clock.Elapsed < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(2000);
            }
        }
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        _run.SwRevision = status.SwRevision;
        _run.AddinVersion = status.AddinVersion;
        Assert.Equal(Protocol.Version, status.ProtocolVersion);
        return $"SOLIDWORKS {status.SwRevision}, add-in {status.AddinVersion}, protocol {status.ProtocolVersion}; documents open: {state.Documents.Count}";
    });

    [SkippableFact, Step(2)]
    public Task A_closed_profile_makes_a_flat_tab_that_reads_back() => _run.CaseAsync(2, "sheetmetal.base_flange of a 100 x 60 mm rectangle, 2 mm at R 3: 12000 mm³, Sheet-Metal and Flat-Pattern, read back with its flat size; a second base flange refused", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var clock = Stopwatch.StartNew();
        var (path, flange) = await Sheet("tab");
        long ms = clock.ElapsedMilliseconds;
        Near(12000, flange.Change.VolumeAfterMm3, "the tab's volume, mm³");
        Assert.Contains(flange.Checks, c => c.Contains("6000 mm² × 2 mm"));
        Assert.Contains(flange.Checks, c => c.Contains("measures 2 mm between the sheet's two faces"));
        Assert.Empty(flange.Sheet.Bends);

        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        var types = tree.Features.Select(f => f.Type).ToList();
        Assert.Contains("SheetMetal", types);
        Assert.Contains("SMBaseFlange", types);
        var flat = tree.Features.Single(f => f.Type == "FlatPattern");
        Assert.True(flat.Suppressed, "the flat pattern is suppressed: the part is folded");
        Assert.Equal("FlatPattern", tree.Features.Where(f => f.Parent == null).Last().Type);

        var read = await Read(path);
        Assert.True(read.SheetMetal);
        Assert.Equal(SheetState.Folded, read.State);
        Near(2, read.ThicknessMm!.Value, "thickness");
        Near(3, read.BendRadiusMm!.Value, "default radius");
        Near(0.5, read.Allowance!.KFactor!.Value, "default K-factor");
        Assert.Equal(1, read.SheetBodies);
        Assert.Equal(0, read.OtherBodies);
        Near(6000, read.BaseFace!.AreaMm2!.Value, "the base face's area, mm²");
        Assert.NotNull(read.Flat);
        Flat(100, read.Flat!.LengthMm, "the flat length");
        Flat(60, read.Flat.WidthMm, "the flat width");

        var second = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "top",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "again", Corner1 = new Point2(0, -100), Corner2 = new Point2(20, -120) } },
        });
        var refused = await Refused(() => Call<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>(new SheetMetalBaseFlangeRequest { Document = path, Sketch = second.Sketch.Name, ThicknessMm = 3, BendRadiusMm = 3 }));
        Assert.Equal("document", refused.Error.Details!["field"]);
        Near(12000, await VolumeOf(path), "the tab after the refusal");
        return $"Base-Flange1 in {ms} ms (sheetmetal deadline 60 s): 12000 mm³, thickness read and measured 2 mm; tree {string.Join(" > ", tree.Features.Where(f => f.Parent == null).Select(f => $"{f.Name} ({f.Type})"))}; " +
               $"flat {read.Flat.LengthMm} × {read.Flat.WidthMm} mm; cut list: {string.Join("; ", read.CutList)}; a second base flange refused ({refused.Message})";
    });

    [SkippableFact, Step(3)]
    public Task An_open_U_bends_at_its_corners_and_flattens_by_the_bend_arithmetic() => _run.CaseAsync(3, "sheetmetal.base_flange of an open U 30/50/30, 2 mm, 20 deep, R 3: two 90° bends, the bend arithmetic's volume, the flat length its legs plus two allowances at K 0.5", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, flange) = await Channel("channel");
        Assert.Equal(2, flange.Sheet.Bends.Count);
        foreach (var b in flange.Sheet.Bends)
        {
            Near(90, b.AngleDeg, $"{b.Name}'s angle", 1e-5);
            Near(3, b.InnerRadiusMm, $"{b.Name}'s radius");
            Near(0.5, b.Allowance.KFactor ?? double.NaN, $"{b.Name}'s K-factor");
        }
        double legs = ChannelLegs(flange);
        Near(20 * 2 * (legs + 4 * Math.PI), flange.Change.VolumeAfterMm3, "the channel's volume, mm³");
        var read = await Read(path);
        Assert.Equal(flange.Sheet.Bends.Select(b => b.Name), read.Bends.Select(b => b.Name));
        Assert.All(read.Bends, b => Near(20, b.WidthMm ?? double.NaN, $"{b.Name}'s width"));
        Assert.NotNull(read.Flat);
        Flat(legs + 2 * HalfK, read.Flat!.LengthMm, "the flat length");
        Flat(20, read.Flat.WidthMm, "the flat width");
        Assert.Equal(2, read.Flat.Bends);
        return $"{string.Join(", ", flange.Sheet.Bends.Select(b => $"{b.Name} {b.AngleDeg:0.###}° R {b.InnerRadiusMm} K {b.Allowance.KFactor}"))}; {flange.Checks.Last()}; " +
               $"flat {read.Flat.LengthMm} × {read.Flat.WidthMm} mm (expected {legs + 2 * HalfK:0.####} × 20); bends {string.Join(", ", read.Bends.Select(b => b.Name))}";
    });

    [SkippableFact, Step(4)]
    public Task Parameters_change_reach_every_default_bend_and_move_the_flat_size() => _run.CaseAsync(4, "sheetmetal.set_parameters on the channel: K 0.1 and 0.9 (CADapter's 82.0265 / 84.5398 relation), radius 8; on the tab: thickness 3 (18000 mm³)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, flange) = await Channel("channel-params");
        double legs = ChannelLegs(flange);
        var notes = new List<string>();
        foreach (double k in new[] { 0.1, 0.9 })
        {
            var set = await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = path, Allowance = new BendAllowance { KFactor = k } });
            Assert.All(set.Sheet.Bends, b => Near(k, b.Allowance.KFactor ?? double.NaN, $"{b.Name}'s K-factor"));
            double expected = legs + 2 * Math.PI / 2 * (3 + k * 2);
            Flat(expected, set.FlatAfter!.LengthMm, $"the flat length at K {k}");
            notes.Add($"K {k}: flat {set.FlatAfter.LengthMm} mm (expected {expected:0.####}); {set.Checks.Last()}");
        }
        var radius = await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = path, BendRadiusMm = 8 });
        Assert.All(radius.Sheet.Bends, b => Near(8, b.InnerRadiusMm, $"{b.Name}'s radius"));
        notes.Add($"radius 8: {string.Join("; ", radius.Checks)}");

        var (tab, _) = await Sheet("tab-params");
        var thicker = await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = tab, ThicknessMm = 3 });
        Near(18000, thicker.Change.VolumeAfterMm3, "the tab at 3 mm, mm³");
        Assert.Contains(thicker.Checks, c => c.Contains("measures 3 mm between the sheet's two faces"));
        notes.Add($"thickness 3: {string.Join("; ", thicker.Checks)}");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(5)]
    public Task A_flattened_part_is_refused_a_change_and_reads_as_flattened() => _run.CaseAsync(5, "the tab flattened through api.call (Flat-Pattern1 unsuppressed): read as flattened, set_parameters refused, nothing changed; folded again", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("flattened");
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        string flat = tree.Features.Single(f => f.Type == "FlatPattern").Reference!;
        await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = path, Target = flat, Member = "SetSuppression2", Args = new List<object?> { 1L, 1L, null } });
        var read = await Read(path);
        Assert.Equal(SheetState.Flattened, read.State);
        var refused = await Refused(() => Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = path, ThicknessMm = 3 }));
        Assert.Equal("flattened", refused.Error.Details!["state"]);
        Near(2, (await Read(path)).ThicknessMm!.Value, "the thickness after the refusal");
        await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = path, Target = flat, Member = "SetSuppression2", Args = new List<object?> { 0L, 1L, null } });
        Assert.Equal(SheetState.Folded, (await Read(path)).State);
        return $"flattened through SetSuppression2 on Flat-Pattern1: read {WireNames.ForEnum(read.State!.Value)}; set_parameters refused ({refused.Message}); folded again";
    });

    [SkippableFact, Step(6)]
    public Task Phase_3_features_work_on_a_sheet_metal_part() => _run.CaseAsync(6, "the Flat-Pattern audit: feature.extrude cuts a 10 mm hole through the tab, feature.fillet rounds a corner's thickness edge, model.tree keeps Flat-Pattern1 last, and neither result names it", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("audit");
        var circle = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = new Point2(50, 30), RadiusMm = 5 } },
        });
        var cut = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = circle.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        Assert.NotEqual("FlatPattern", (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Single(f => f.Name == cut.Feature.Name).Type);
        Near(12000 - Math.PI * 25 * 2, cut.Change.VolumeAfterMm3, "the tab with its hole, mm³");
        var box = await BoxOf(path);
        double z = (box.Min.Z + box.Max.Z) / 2;
        var edge = Assert.Single((await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = path, Kind = TopologyKind.Edge, Near = new Point3(0, 0, z), MaxDistanceMm = 0.001 })).Entities);
        var fillet = await Call<FeatureFilletRequest, FeatureResult>(new FeatureFilletRequest { Document = path, Edges = new List<string> { edge.Reference! }, RadiusMm = 5 });
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        var top = tree.Features.Where(f => f.Parent == null).ToList();
        Assert.Equal("FlatPattern", top.Last().Type);
        Assert.Contains(top, f => f.Name == cut.Feature.Name);
        Assert.Contains(top, f => f.Name == fillet.Feature.Name);
        Assert.DoesNotContain(cut.Change.Created.Concat(fillet.Change.Created), c => c.Name.StartsWith("Flat-Pattern", StringComparison.Ordinal));
        double removed = 2 * (25 - Math.PI * 25 / 4);
        Near(12000 - Math.PI * 25 * 2 - removed, fillet.Change.VolumeAfterMm3, "the tab with its hole and a 5 mm corner, mm³", 1e-5);
        return $"{cut.Feature.Name} ({cut.Change.VolumeAfterMm3:0.###} mm³), {fillet.Feature.Name} ({fillet.Change.VolumeAfterMm3:0.###} mm³); tree ends {string.Join(" > ", top.Skip(Math.Max(0, top.Count - 4)).Select(f => f.Name))}";
    });

    [SkippableFact, Step(7)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(7, "the suite's documents closed", async () =>
    {
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        int closed = 0;
        foreach (var d in state.Documents.Where(d => d.Path != null))
        {
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true });
            closed++;
        }
        var open = (await _run.AddinStateAsync()).Documents;
        Assert.True(open.Count == 0, $"still open after closing {closed}: {string.Join(", ", open.Select(d => d.Path ?? d.Title))}");
        return $"closed {closed} documents; none open";
    });
}
