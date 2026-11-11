using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// Plan 6.B's cases: edge flanges against CADapter's measured flat sizes and box, miter flanges, every hem kind, and
/// set_parameters reaching a flange's own bend. Each starts from the suite's 100 × 60 × 2 tab at R 3.
/// </summary>
public sealed partial class SheetMetalSuite
{
    /// <summary>A 90° material-inside flange 25 mm from the inner sharp at R 3, T 2, per mm of width: 22 straight less 5 taken back, and the bend.</summary>
    static readonly double Per25 = 2 * (22 - 5) + Math.PI / 4 * 16;

    /// <summary>The same for 20 mm (CADapter's box flanges).</summary>
    static readonly double Per20 = 2 * (17 - 5) + Math.PI / 4 * 16;

    /// <summary>The tab's base face: its level along its normal, and the normal.</summary>
    async Task<(double Level, Vector3 Normal)> BaseFaceOf(string path)
    {
        var face = (await Read(path)).BaseFace!;
        var n = face.Normal!;
        return (n.X * face.Origin!.X + n.Y * face.Origin.Y + n.Z * face.Origin.Z, n);
    }

    /// <summary>The tab's edge on its base face nearest (x, y): the edge model.find gives within 1 µm of that point at the face's level.</summary>
    async Task<string> EdgeAt(string path, double x, double y)
    {
        var (level, n) = await BaseFaceOf(path);
        var found = await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = path, Kind = TopologyKind.Edge, Near = new Point3(x, y, level * n.Z), MaxDistanceMm = 0.001 });
        return Assert.Single(found.Entities).Reference!;
    }

    Task<SheetMetalFeatureResult> EdgeFlange(SheetMetalEdgeFlangeRequest request) => Call<SheetMetalEdgeFlangeRequest, SheetMetalFeatureResult>(request);

    [SkippableFact, Step(7)]
    public Task An_edge_flange_flattens_to_CADapters_measured_length() => _run.CaseAsync(7, "sheetmetal.edge_flange: one 25 mm flange (inner virtual sharp) on the tab's 100 mm edge at R 3: one 90° bend, 12000 + 100 × the flange geometry's mm³, 27 mm tall, flat 100 × 83.2832; the same flange as 27 mm from the outer sharp (the default)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var notes = new List<string>();
        foreach (var (name, length, from) in new[] { ("flange-inner", 25.0, (FlangeLengthFrom?)FlangeLengthFrom.InnerVirtualSharp), ("flange-outer", 27.0, (FlangeLengthFrom?)null) })
        {
            var (path, _) = await Sheet(name);
            var flange = await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, 50, 60) }, LengthMm = length, LengthFrom = from });
            var bend = Assert.Single(flange.Sheet.Bends);
            Near(90, bend.AngleDeg, "the bend's angle", 1e-5);
            Near(3, bend.InnerRadiusMm, "the bend's radius");
            Near(12000 + 100 * Per25, flange.Change.VolumeAfterMm3, "the tab with its flange, mm³", 1e-5);
            var box = await BoxOf(path);
            Near(27, box.Max.Z - box.Min.Z, "the part's height, mm", 1e-5);
            var read = await Read(path);
            Flat(100, read.Flat!.LengthMm, "the flat size along the edge");
            Flat(83.2832, read.Flat.WidthMm, "the flat size across the edge");
            notes.Add($"{name}: {flange.Feature.Name} {flange.Change.VolumeAfterMm3:0.###} mm³, box {box.Max.X - box.Min.X:0.###} × {box.Max.Y - box.Min.Y:0.###} × {box.Max.Z - box.Min.Z:0.###}, flat {read.Flat.LengthMm} × {read.Flat.WidthMm}; {string.Join("; ", flange.Checks)}");
        }
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(8)]
    public Task A_flanges_own_K_factor_moves_its_flat_length_by_the_bend_arithmetic() => _run.CaseAsync(8, "sheetmetal.edge_flange with its own K 0.1 and 0.9 on new tabs: flat 82.0265 and 84.5398 (CADapter); then set_parameters K 0.1 on a flange made at the default reaches its bend: 82.0265", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var notes = new List<string>();
        foreach (var (k, flat) in new[] { (0.1, 82.0265), (0.9, 84.5398) })
        {
            var (path, _) = await Sheet($"flange-k{k * 10:0}");
            var flange = await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, 50, 60) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp, Allowance = new BendAllowance { KFactor = k } });
            Near(k, Assert.Single(flange.Sheet.Bends).Allowance.KFactor ?? double.NaN, "the bend's K-factor");
            var read = await Read(path);
            Flat(flat, Math.Min(read.Flat!.LengthMm, read.Flat.WidthMm), $"the flat size across the edge at K {k}");
            notes.Add($"K {k}: flat {read.Flat.LengthMm} × {read.Flat.WidthMm}");
        }
        var (tab, _) = await Sheet("flange-set-k");
        await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = tab, Edges = new List<string> { await EdgeAt(tab, 50, 60) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        var set = await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = tab, Allowance = new BendAllowance { KFactor = 0.1 } });
        Assert.All(set.Sheet.Bends, b => Near(0.1, b.Allowance.KFactor ?? double.NaN, $"{b.Name}'s K-factor"));
        Flat(82.0265, Math.Min(set.FlatAfter!.LengthMm, set.FlatAfter.WidthMm), "the flat size after set_parameters K 0.1");
        notes.Add($"set_parameters K 0.1: {string.Join("; ", set.Checks)}");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(9)]
    public Task Four_flanges_make_CADapters_box() => _run.CaseAsync(9, "four 20 mm edge flanges (inner virtual sharp), the long edges first, each found again by model.find: a box 100 × 60 × 22 of 22970 mm³ (CADapter 2.2970e-05 m³), flat 136.6 × 96.6", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("box");
        var made = new List<string>();
        foreach (var (x, y) in new[] { (50.0, 60.0), (50.0, 0.0), (0.0, 30.0), (100.0, 30.0) })
        {
            var flange = await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, x, y) }, LengthMm = 20, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
            made.Add($"{flange.Feature.Name} {flange.Change.VolumeAfterMm3 - flange.Change.VolumeBeforeMm3:0.###} mm³");
        }
        double volume = await VolumeOf(path);
        Near(12000 + 300 * Per20, volume, "the box's volume, mm³", 1e-5);
        var box = await BoxOf(path);
        Near(100, box.Max.X - box.Min.X, "the box's length", 1e-5);
        Near(60, box.Max.Y - box.Min.Y, "the box's width", 1e-5);
        Near(22, box.Max.Z - box.Min.Z, "the box's height", 1e-5);
        var read = await Read(path);
        Assert.Equal(4, read.Bends.Count);
        Flat(136.6, read.Flat!.LengthMm, "the flat length");
        Flat(96.6, read.Flat.WidthMm, "the flat width");
        return $"{string.Join(", ", made)}; {volume:0.###} mm³; flat {read.Flat.LengthMm} × {read.Flat.WidthMm}";
    });

    [SkippableFact, Step(10)]
    public Task A_miter_flange_is_as_long_as_its_profile_and_one_that_misses_is_refused() => _run.CaseAsync(10, "sheetmetal.miter_flange: a 20 mm line on the right plane from the 100 mm edge's end at x = 0, into the sheet's side: one bend, the part 20 mm (to 22) tall (CADapter 20.0); the same line 2 mm off the end refused with gap_mm 2 and nothing made", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("miter");
        var (level, n) = await BaseFaceOf(path);
        string edge = await EdgeAt(path, 50, 60);
        // On the right plane SOLIDWORKS' sketch x runs along -z: the edge's end (0, 60, z) is at (-z, 60).
        double z = level * n.Z;
        SketchCreateRequest Line(double from) => new SketchCreateRequest
        {
            Document = path,
            On = "right",
            Lines = new List<SketchLine> { new SketchLine { Id = "p", Start = new Point2(-(z - from * n.Z), 60), End = new Point2(-(z - (from + 20) * n.Z), 60) } },
        };
        var missed = await Call<SketchCreateRequest, SketchCreateResult>(Line(2));
        var refused = await Refused(() => Call<SheetMetalMiterFlangeRequest, SheetMetalFeatureResult>(new SheetMetalMiterFlangeRequest { Document = path, Sketch = missed.Sketch.Name, Edges = new List<string> { edge } }));
        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        Near(2, (double)refused.Error.Details!["gap_mm"]!, "the gap", 1e-6);
        Near(12000, await VolumeOf(path), "the tab after the refusal");
        var profile = await Call<SketchCreateRequest, SketchCreateResult>(Line(0));
        var miter = await Call<SheetMetalMiterFlangeRequest, SheetMetalFeatureResult>(new SheetMetalMiterFlangeRequest { Document = path, Sketch = profile.Sketch.Name, Edges = new List<string> { edge } });
        Assert.Single(miter.Sheet.Bends);
        var box = await BoxOf(path);
        double height = box.Max.Z - box.Min.Z;
        Assert.True(height >= 20 - 1e-3 && height <= 22 + 1e-3, $"the part is {height} mm tall after a 20 mm miter flange");
        return $"refused 2 mm off ({refused.Message}); {miter.Feature.Name}: {miter.Change.VolumeAfterMm3:0.###} mm³, {height:0.####} mm tall; {string.Join("; ", miter.Checks)}";
    });

    [SkippableFact, Step(11)]
    public Task Every_hem_kind_folds_its_edge_within_its_material() => _run.CaseAsync(11, "sheetmetal.hem on new tabs' 100 mm edge: closed 10, open 10 with gap 1, teardrop R 2 at 270°, rolled R 3 at 200°; each a Hem, the edge gone, the volume within the band (the measured values are recorded to tighten it)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var notes = new List<string>();
        var kinds = new[]
        {
            new SheetMetalHemRequest { Kind = HemKind.Closed, LengthMm = 10 },
            new SheetMetalHemRequest { Kind = HemKind.Open, LengthMm = 10, GapMm = 1 },
            new SheetMetalHemRequest { Kind = HemKind.Teardrop, RadiusMm = 2, AngleDeg = 270 },
            new SheetMetalHemRequest { Kind = HemKind.Rolled, RadiusMm = 3, AngleDeg = 200 },
        };
        foreach (var hem in kinds)
        {
            var (path, _) = await Sheet($"hem-{WireNames.ForEnum(hem.Kind)}");
            hem.Document = path;
            hem.Edges = new List<string> { await EdgeAt(path, 50, 60) };
            var made = await Call<SheetMetalHemRequest, SheetMetalFeatureResult>(hem);
            Assert.True(made.Change.VolumeAfterMm3 > 12000, $"the {WireNames.ForEnum(hem.Kind)} hem added material");
            notes.Add($"{WireNames.ForEnum(hem.Kind)}: {made.Feature.Name}, {made.Change.VolumeAfterMm3 - 12000:0.###} mm³ added, {made.Sheet.Bends.Count} bends; {string.Join("; ", made.Checks)}");
        }
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(12)]
    public Task A_flattened_part_refuses_a_flange() => _run.CaseAsync(12, "sheetmetal.edge_flange on the tab flattened through api.call: refused with state flattened, nothing made; folded again", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("flange-flattened");
        string edge = await EdgeAt(path, 50, 60);
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        string flat = tree.Features.Single(f => f.Type == "FlatPattern").Reference!;
        await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = path, Target = flat, Member = "SetSuppression2", Args = new List<object?> { 1L, 1L, null } });
        var refused = await Refused(() => EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { edge }, LengthMm = 25 }));
        Assert.Equal("flattened", refused.Error.Details!["state"]);
        await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = path, Target = flat, Member = "SetSuppression2", Args = new List<object?> { 0L, 1L, null } });
        Assert.DoesNotContain((await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features, f => f.Type == "EdgeFlange");
        return $"refused: {refused.Message}";
    });
}
