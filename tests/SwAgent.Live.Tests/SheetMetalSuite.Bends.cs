using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// Plan 6.C's cases: sketched bends against CADapter's measured box, flatten and fold with the flatten volume identity,
/// unfold, a cut across the bend and fold, convert of CADapter's L, the refusals, and the user_editing cases (one by
/// hand). Each starts from the suite's 100 × 60 × 2 tab at R 3 or its own part.
/// </summary>
public sealed partial class SheetMetalSuite
{
    /// <summary>A 90° bend at R 3, T 2 and K 0.1 develops π/2 × 0.8 mm short of its middle arc.</summary>
    static readonly double ShortAtK01 = Math.PI / 2 * 0.8;

    /// <summary>The tab's base face by reference, with the sketch frame SOLIDWORKS gives a sketch on it (read from a probe sketch's one construction line).</summary>
    async Task<(string Face, SketchSummary Frame)> BaseSketchFrame(string path)
    {
        var face = (await Read(path)).BaseFace!;
        var probe = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = face.Reference!,
            Lines = new List<SketchLine> { new SketchLine { Id = "probe", Start = new Point2(0, 0), End = new Point2(1, 0), Construction = true } },
        });
        return (face.Reference!, probe.Sketch);
    }

    /// <summary>A model point in a sketch's coordinates.</summary>
    static Point2 In(SketchSummary s, Point3 p)
    {
        double dx = p.X - s.Origin.X, dy = p.Y - s.Origin.Y, dz = p.Z - s.Origin.Z;
        return new Point2(dx * s.XAxis.X + dy * s.XAxis.Y + dz * s.XAxis.Z, dx * s.YAxis.X + dy * s.YAxis.Y + dz * s.YAxis.Z);
    }

    /// <summary>Lines across the tab at these x (y from -10 to 70, CADapter's), on its base face: the sketch's name and the base face.</summary>
    async Task<(string Sketch, string Face)> BendLines(string path, params double[] xs)
    {
        var (face, frame) = await BaseSketchFrame(path);
        var (level, n) = await BaseFaceOf(path);
        double z = level * n.Z;
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = face,
            Lines = xs.Select((x, i) => new SketchLine { Id = $"bend{i + 1}", Start = In(frame, new Point3(x, -10, z)), End = In(frame, new Point3(x, 70, z)) }).ToList(),
        });
        return (sketch.Sketch.Name, face);
    }

    static (double X, double Y, double Z) Size(Box3 b) => (b.Max.X - b.Min.X, b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z);

    Task<SheetMetalFlattenResult> Flatten(string path, bool flat) => Call<SheetMetalFlattenRequest, SheetMetalFlattenResult>(new SheetMetalFlattenRequest { Document = path, Flat = flat });

    [SkippableFact, Step(13)]
    public Task A_sketched_bend_makes_CADapters_box_and_keeps_the_flat_size() => _run.CaseAsync(13, "sheetmetal.sketched_bend: a line at x = 60 across the tab, 90° bend_outside at R 3: one bend, 12000 mm³ (K 0.5), CADapter's box 65 × 60 × 38.7168, flat 100 × 60; the same at K 0.1 folds 60 × 2 × π/2 × 0.8 mm³ more", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("sketched-bend");
        var (sketch, face) = await BendLines(path, 60);
        var clock = Stopwatch.StartNew();
        var bend = await Call<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>(new SheetMetalSketchedBendRequest { Document = path, Sketch = sketch, FixedFace = face, AngleDeg = 90 });
        long ms = clock.ElapsedMilliseconds;
        var made = Assert.Single(bend.Sheet.Bends);
        Near(90, made.AngleDeg, "the bend's angle", 1e-5);
        Near(3, made.InnerRadiusMm, "the bend's radius");
        Near(12000, bend.Change.VolumeAfterMm3, "the bent tab's volume, mm³", 1e-5);
        // bend_outside: the bend starts at the line, so the part reaches R + T = 5 mm past it; the leg left is 40 less its allowance π/2 × 4.
        var size = Size(await BoxOf(path));
        Near(65, size.X, "the bent tab's length", 1e-5);
        Near(60, size.Y, "the bent tab's width", 1e-5);
        Near(5 + 40 - HalfK, size.Z, "the bent tab's height (CADapter 38.7)", 1e-5);
        var read = await Read(path);
        Flat(100, read.Flat!.LengthMm, "the flat length after the sketched bend");
        Flat(60, read.Flat.WidthMm, "the flat width after the sketched bend");
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        string type = tree.Features.Single(f => f.Name == bend.Feature.Name).Type ?? "?";

        var (k01, _) = await Sheet("sketched-bend-k01");
        var (sketch01, face01) = await BendLines(k01, 60);
        var bend01 = await Call<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>(new SheetMetalSketchedBendRequest { Document = k01, Sketch = sketch01, FixedFace = face01, AngleDeg = 90, Allowance = new BendAllowance { KFactor = 0.1 } });
        Near(12000 + 60 * 2 * ShortAtK01, bend01.Change.VolumeAfterMm3, "the tab bent at K 0.1, mm³", 1e-5);
        return $"{bend.Feature.Name} ({type}) in {ms} ms, {made.Name}: {bend.Change.VolumeAfterMm3:0.###} mm³, box {size.X:0.####} × {size.Y:0.####} × {size.Z:0.####}, flat {read.Flat.LengthMm} × {read.Flat.WidthMm}; {string.Join("; ", bend.Checks)} | K 0.1: {bend01.Change.VolumeAfterMm3:0.###} mm³";
    });

    [SkippableFact, Step(14)]
    public Task Two_lines_make_a_channel_in_one_call() => _run.CaseAsync(14, "sheetmetal.sketched_bend: lines at x = 30 and 70 in one sketch, the middle kept: two bends, 12000 mm³, a channel 50 × 60 × 28.7168", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("sketched-channel");
        var (sketch, face) = await BendLines(path, 30, 70);
        var bend = await Call<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>(new SheetMetalSketchedBendRequest { Document = path, Sketch = sketch, FixedFace = face, AngleDeg = 90 });
        Assert.Equal(2, bend.Sheet.Bends.Count);
        Near(12000, bend.Change.VolumeAfterMm3, "the channel's volume, mm³", 1e-5);
        var size = Size(await BoxOf(path));
        Near(40 + 2 * 5, size.X, "the channel's width", 1e-5);
        Near(5 + 30 - HalfK, size.Z, "the channel's legs", 1e-5);
        return $"{string.Join(", ", bend.Sheet.Bends.Select(b => b.Name))}; box {size.X:0.####} × {size.Y:0.####} × {size.Z:0.####}";
    });

    [SkippableFact, Step(15)]
    public Task Flattening_keeps_the_volume_at_half_and_moves_it_by_the_bend_arithmetic_elsewhere() => _run.CaseAsync(15, "sheetmetal.flatten: CADapter's box of four 20 mm flanges flattens to 136.5664 × 96.5664 × 2 at the same 22969.911 mm³ and folds back to 100 × 60 × 22; the channel at K 0.1 flattens 2 × 20 × 2 × π/2 × 0.8 mm³ lighter", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("flatten-box");
        foreach (var (x, y) in new[] { (50.0, 60.0), (50.0, 0.0), (0.0, 30.0), (100.0, 30.0) })
            await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, x, y) }, LengthMm = 20, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        double folded = await VolumeOf(path);
        var clock = Stopwatch.StartNew();
        var flat = await Flatten(path, true);
        long ms = clock.ElapsedMilliseconds;
        Assert.Equal(SheetState.Flattened, flat.State);
        Near(folded, flat.Change.VolumeAfterMm3, "the flattened box's volume, mm³", 1e-5);
        double across = 2 * Math.PI + 12;
        var size = Size(await BoxOf(path));
        var sides = new[] { size.X, size.Y, size.Z }.OrderBy(v => v).ToArray();
        Near(2, sides[0], "the flat box's thickness", 1e-5);
        Near(60 + 2 * across, sides[1], "the flat box's width", 1e-5);
        Near(100 + 2 * across, sides[2], "the flat box's length", 1e-5);
        Assert.Equal(SheetState.Flattened, (await Read(path)).State);
        var again = await Flatten(path, true);
        Assert.True(again.Unchanged);
        var fold = await Flatten(path, false);
        Assert.Equal(SheetState.Folded, fold.State);
        Near(folded, fold.Change.VolumeAfterMm3, "the box folded again, mm³", 1e-5);
        var back = Size(await BoxOf(path));
        Near(22, back.Z, "the box's height folded again", 1e-5);

        var (channel, _) = await Channel("flatten-channel");
        await Call<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>(new SheetMetalSetParametersRequest { Document = channel, Allowance = new BendAllowance { KFactor = 0.1 } });
        double channelFolded = await VolumeOf(channel);
        var channelFlat = await Flatten(channel, true);
        Near(channelFolded - 2 * 20 * 2 * ShortAtK01, channelFlat.Change.VolumeAfterMm3, "the channel flattened at K 0.1, mm³", 1e-5);
        await Flatten(channel, false);
        return $"box flattened in {ms} ms: {sides[2]:0.####} × {sides[1]:0.####} × {sides[0]:0.####}, flat {flat.Flat?.LengthMm} × {flat.Flat?.WidthMm}; {string.Join("; ", flat.Checks)} | channel at K 0.1: {channelFolded:0.###} -> {channelFlat.Change.VolumeAfterMm3:0.###} mm³";
    });

    [SkippableFact, Step(16)]
    public Task A_hole_cut_across_an_unfolded_bend_folds_with_it() => _run.CaseAsync(16, "sheetmetal.unfold of a 25 mm flange's bend on the base face, a 6 mm hole cut across the flat bend (π × 36 × 2 mm³), sheetmetal.fold: the part 27 mm tall again at its folded volume less the hole", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("unfold-cut-fold");
        await EdgeFlange(new SheetMetalEdgeFlangeRequest { Document = path, Edges = new List<string> { await EdgeAt(path, 50, 60) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp });
        double folded = await VolumeOf(path);
        var face = (await Read(path)).BaseFace!.Reference!;
        var unfold = await Call<SheetMetalUnfoldRequest, SheetMetalFeatureResult>(new SheetMetalUnfoldRequest { Document = path, FixedFace = face });
        Near(folded, unfold.Change.VolumeAfterMm3, "the part unfolded, mm³", 1e-5);
        Near(2, Size(await BoxOf(path)).Z, "the unfolded part's thickness", 1e-5);
        // The bend lies flat from y = 55 (the sheet the flange took back) for its allowance of 2π mm: a 6 mm hole at y = 58 spans it.
        var circle = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = new Point2(50, 58), RadiusMm = 6 } },
        });
        var cut = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest
        {
            Document = path,
            Sketch = circle.Sketch.Name,
            Mode = FeatureMode.Cut,
            End = ExtrudeEnd.ThroughAll,
            Direction2 = new ExtrudeDirection { End = ExtrudeEnd.ThroughAll },
        });
        double hole = Math.PI * 36 * 2;
        Near(folded - hole, cut.Change.VolumeAfterMm3, "the unfolded part with its hole, mm³", 1e-5);
        var fold = await Call<SheetMetalFoldRequest, SheetMetalFeatureResult>(new SheetMetalFoldRequest { Document = path });
        Near(folded - hole, fold.Change.VolumeAfterMm3, "the part folded with the hole in its bend, mm³", 1e-5);
        Near(27, Size(await BoxOf(path)).Z, "the part's height folded again", 1e-5);
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        string Type(string name) => tree.Features.Single(f => f.Name == name).Type ?? "?";
        return $"{unfold.Feature.Name} ({Type(unfold.Feature.Name!)}): {string.Join("; ", unfold.Checks)} | {cut.Feature.Name} {cut.Change.VolumeAfterMm3:0.###} mm³ | {fold.Feature.Name} ({Type(fold.Feature.Name!)}): {string.Join("; ", fold.Checks)}";
    });

    /// <summary>A new part with CADapter's L: 60 × 40 mm outside, a 2 mm wall, extruded 50 mm from the front plane.</summary>
    async Task<string> L(string name)
    {
        string path = NewPart(name);
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var outline = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Lines = new List<SketchLine>
            {
                new SketchLine { Id = "a", Start = new Point2(0, 0), End = new Point2(60, 0) },
                new SketchLine { Id = "b", Start = new Point2(60, 0), End = new Point2(60, 2) },
                new SketchLine { Id = "c", Start = new Point2(60, 2), End = new Point2(2, 2) },
                new SketchLine { Id = "d", Start = new Point2(2, 2), End = new Point2(2, 40) },
                new SketchLine { Id = "e", Start = new Point2(2, 40), End = new Point2(0, 40) },
                new SketchLine { Id = "f", Start = new Point2(0, 40), End = new Point2(0, 0) },
            },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = outline.Sketch.Name, DepthMm = 50 });
        return path;
    }

    async Task<string> FindOne(string path, TopologyKind kind, Point3 near) =>
        Assert.Single((await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = path, Kind = kind, Near = near, MaxDistanceMm = 0.001 })).Entities).Reference!;

    [SkippableFact, Step(17)]
    public Task CADapters_L_converts_keeping_its_box_and_flattens_to_its_measured_length() => _run.CaseAsync(17, "sheetmetal.convert of an extruded L 60 × 40 × 50 (2 mm wall) from its inner floor face at R 3: one bend, the box unchanged, 9000 + 50 × π/4 × 16 mm³, flat 96.2832 × 50 (CADapter 96.3); the outer face refused first with nothing made", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = await L("convert-l");
        var boxBefore = Size(await BoxOf(path));
        string edge = await FindOne(path, TopologyKind.Edge, new Point3(2, 2, 25));
        string inner = await FindOne(path, TopologyKind.Face, new Point3(31, 2, 25));
        string outer = await FindOne(path, TopologyKind.Face, new Point3(30, 0, 25));
        var refused = await Refused(() => Call<SheetMetalConvertRequest, SheetMetalFeatureResult>(new SheetMetalConvertRequest { Document = path, FixedFace = outer, BendEdges = new List<string> { edge }, ThicknessMm = 2, BendRadiusMm = 3 }));
        Assert.Equal("fixed_face", refused.Error.Details!["field"]);
        Assert.False((await Read(path)).SheetMetal);
        var clock = Stopwatch.StartNew();
        var convert = await Call<SheetMetalConvertRequest, SheetMetalFeatureResult>(new SheetMetalConvertRequest { Document = path, FixedFace = inner, BendEdges = new List<string> { edge }, ThicknessMm = 2, BendRadiusMm = 3 });
        long ms = clock.ElapsedMilliseconds;
        var bend = Assert.Single(convert.Sheet.Bends);
        Near(3, bend.InnerRadiusMm, "the bend's radius");
        var box = Size(await BoxOf(path));
        Near(boxBefore.X, box.X, "the L's length after the conversion", 1e-5);
        Near(boxBefore.Y, box.Y, "the L's height after the conversion", 1e-5);
        Near(boxBefore.Z, box.Z, "the L's depth after the conversion", 1e-5);
        // The inner faces are the sheet's: legs of 58 − 3 and 38 − 3 mm and a 90° bend at R 3, 2 mm thick and 50 deep.
        Near(50 * 2 * (55 + 35) + 50 * Math.PI / 4 * 16, convert.Change.VolumeAfterMm3, "the converted L's volume, mm³", 1e-5);
        var read = await Read(path);
        Flat(55 + 35 + HalfK, read.Flat!.LengthMm, "the converted L's flat length (CADapter 96.3)");
        Flat(50, read.Flat.WidthMm, "the converted L's flat width");
        var flat = await Flatten(path, true);
        var sides = new[] { Size(await BoxOf(path)).X, Size(await BoxOf(path)).Y, Size(await BoxOf(path)).Z }.OrderBy(v => v).ToArray();
        Near(2, sides[0], "the flat L's thickness", 1e-5);
        Near(55 + 35 + HalfK, sides[2], "the flat L's length", 1e-5);
        await Flatten(path, false);
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        return $"outer face refused ({refused.Message}); {convert.Feature.Name} in {ms} ms, tree {string.Join(" > ", tree.Features.Where(f => f.Parent == null).Select(f => $"{f.Name} ({f.Type})"))}; {convert.Change.VolumeAfterMm3:0.###} mm³; flat {read.Flat.LengthMm} × {read.Flat.WidthMm}, box flat {sides[2]:0.####} × {sides[1]:0.####} × {sides[0]:0.####}; {string.Join("; ", convert.Checks)} | flatten: {string.Join("; ", flat.Checks)}";
    });

    [SkippableFact, Step(18)]
    public Task What_cannot_be_done_is_refused_and_a_sketch_open_for_editing_makes_a_bend_wait() => _run.CaseAsync(18, "refusals with nothing made: convert on a sheet-metal part, fold with nothing unfolded, a sketched bend on a flattened part; busy with user_editing for a sketched bend while a sketch is open for editing (diag.edit_sketch)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("bend-refusals");
        var (sketch, face) = await BendLines(path, 60);
        var notes = new List<string>();
        var convert = await Refused(() => Call<SheetMetalConvertRequest, SheetMetalFeatureResult>(new SheetMetalConvertRequest { Document = path, FixedFace = face, BendEdges = new List<string> { "r1.edge.none" }, ThicknessMm = 2, BendRadiusMm = 3 }));
        Assert.Equal("document", convert.Error.Details!["field"]);
        notes.Add($"convert: {convert.Message}");
        var fold = await Refused(() => Call<SheetMetalFoldRequest, SheetMetalFeatureResult>(new SheetMetalFoldRequest { Document = path }));
        Assert.Equal("bends", fold.Error.Details!["field"]);
        notes.Add($"fold: {fold.Message}");
        await Flatten(path, true);
        var flat = await Refused(() => Call<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>(new SheetMetalSketchedBendRequest { Document = path, Sketch = sketch, FixedFace = face, AngleDeg = 90 }));
        Assert.Equal("flattened", flat.Error.Details!["state"]);
        Assert.Contains("sw_sheetmetal_flatten", flat.Message);
        notes.Add($"flattened: {flat.Message}");
        await Flatten(path, false);
        var open = await Call<DiagEditSketchRequest, DiagEditingResult>(new DiagEditSketchRequest { Document = path, Sketch = sketch, Open = true });
        SwAgentException busy;
        try
        {
            Assert.Equal(sketch, open.Sketch);
            busy = await Refused(() => Call<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>(new SheetMetalSketchedBendRequest { Document = path, Sketch = sketch, FixedFace = face, AngleDeg = 90 }));
        }
        finally
        {
            await Call<DiagEditSketchRequest, DiagEditingResult>(new DiagEditSketchRequest { Document = path, Open = false });
        }
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("user_editing", busy.Error.Details!["reason"]);
        notes.Add($"busy: {busy.Message}");
        Near(12000, await VolumeOf(path), "the tab after the refusals");
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path });
        Assert.DoesNotContain(tree.Features, f => f.Type == "SM3dBend" || f.Type == "SketchBend" || f.Type == "Fold" || f.Type == "SolidToSheetMetal");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(19)]
    public Task A_command_the_user_has_open_makes_flatten_wait() => _run.CaseAsync(19, "busy with user_editing for sheetmetal.flatten while the user has a PropertyManager open (by hand)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (path, _) = await Sheet("flatten-busy");
        _run.Say("ACTION: in SOLIDWORKS, in flatten-busy.SLDPRT, click Features > Extruded Boss/Base so its PropertyManager opens (waiting up to 3 min); leave it open until the run asks you to close it");
        var clock = Stopwatch.StartNew();
        DiagEditingResult seen;
        while ((seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = path })).Command == null)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw new SkipException("no PropertyManager was seen open within 3 min");
            await Task.Delay(2000);
        }
        var busy = await Refused(() => Flatten(path, true));
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("user_editing", busy.Error.Details!["reason"]);
        Assert.Equal(seen.Command, busy.Error.Details["command"]);
        _run.Say("ACTION: close the PropertyManager with its red X (Cancel) (waiting up to 3 min)");
        clock.Restart();
        while ((seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = path })).Command != null || seen.Sketch != null)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw _run.Stop($"the user's {seen.Command ?? seen.Sketch} was still open after 3 min");
            await Task.Delay(2000);
        }
        Assert.Equal(SheetState.Folded, (await Read(path)).State);
        var flat = await Flatten(path, true);
        Assert.Equal(SheetState.Flattened, flat.State);
        await Flatten(path, false);
        return $"busy, user_editing, command '{busy.Error.Details["command"]}', detected before ({busy.Message}); flattened {clock.Elapsed.TotalSeconds:0} s after asking for it to be closed";
    });
}
