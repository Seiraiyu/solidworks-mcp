using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The parts suite's 3.D cases (design 371, Testing: "each feature with an exact volume or count check"): sweep, loft,
/// rib, shell, draft, Hole Wizard, linear and circular patterns, mirror and configurations, each on a part of its own in
/// the scratch directory, checked against the volume the geometry gives. Every volume below is worked from the
/// 40 x 20 x 10 mm block (x 0 to 40, y 0 to 20, z 0 to 10) that diag.scratch_block makes, or from the sketch drawn.
/// </summary>
public sealed partial class PartsSuite
{
    Task<FeatureResult> Plane(string path, string from, double distance) =>
        Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Offset, From = from, DistanceMm = distance });

    /// <summary>
    /// A plane offset from <paramref name="from"/> to where <paramref name="coordinate"/> of its origin is
    /// <paramref name="at"/>: which way an offset goes is SOLIDWORKS' choice, so a plane made the other way is deleted
    /// and made again flipped. Its name.
    /// </summary>
    async Task<string> PlaneAt(string path, string from, double at, Func<Point3, double> coordinate)
    {
        var plane = await Plane(path, from, at);
        if (Math.Abs(coordinate(plane.Feature.Origin!) - at) < 1e-6) return plane.Feature.Name!;
        await Call<FeatureDeleteRequest, FeatureDeleteResult>(new FeatureDeleteRequest { Document = path, Features = new List<string> { plane.Feature.Name! } });
        var flipped = await Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Offset, From = from, DistanceMm = at, Flip = true });
        Near(at, coordinate(flipped.Feature.Origin!), $"the plane {at} mm from {from}, flipped");
        return flipped.Feature.Name!;
    }

    Task<SketchCreateResult> Circle(string path, string on, double x, double y, double r) =>
        Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = on,
            Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = new Point2(x, y), RadiusMm = r } },
        });

    /// <summary>A through hole of radius <paramref name="r"/> at (x, y) in the block, cut from a sketch on the front plane; its feature's name.</summary>
    async Task<string> Through(string path, double x, double y, double r)
    {
        var sketch = await Circle(path, "front", x, y, r);
        var cut = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = sketch.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        return cut.Feature.Name!;
    }

    [SkippableFact, Step(22)]
    public Task A_sweep_along_a_line_is_a_cylinder() => _run.CaseAsync(22, "feature.sweep: a 5 mm radius circle swept 30 mm along a line normal to it, a cylinder of 2356.194 mm³", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("sweep");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var profile = await Circle(path, "front", 0, 0, 5);
        // On the top plane the sketch's y runs along -z (as the unit tests' FakeSketch takes it; this case's box measures it), so (0, -30) is 30 mm off the front plane.
        var line = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "top",
            Lines = new List<SketchLine> { new SketchLine { Id = "path", Start = new Point2(0, 0), End = new Point2(0, -30) } },
        });
        var sweep = await Call<FeatureSweepRequest, FeatureResult>(new FeatureSweepRequest { Document = path, Profile = profile.Sketch.Name, Path = line.Sketch.Name });
        Near(Math.PI * 25 * 30, await VolumeOf(path), "the swept cylinder's volume");
        Assert.Contains(sweep.Checks, c => c.Contains("along its normal"));
        var box = await BoxOf(path);
        return $"{sweep.Feature.Name}: {string.Join("; ", sweep.Checks)}; box z {box.Min.Z:0.###} to {box.Max.Z:0.###} (the top plane's sketch y is model {(box.Max.Z > 1 ? "-z, as assumed" : "+z: correct FakeSketch.Axes")})";
    });

    [SkippableFact, Step(23)]
    public Task A_loft_between_two_squares_is_a_frustum() => _run.CaseAsync(23, "feature.loft: a 20 mm square to a 10 mm square 10 mm above it, a frustum of 2333.333 mm³", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("loft");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var plane = await Plane(path, "front", 10);
        SketchCreateRequest Square(string on, double half) => new SketchCreateRequest
        {
            Document = path,
            On = on,
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "sq", Corner1 = new Point2(-half, -half), Corner2 = new Point2(half, half) } },
        };
        var big = await Call<SketchCreateRequest, SketchCreateResult>(Square("front", 10));
        var small = await Call<SketchCreateRequest, SketchCreateResult>(Square(plane.Feature.Name!, 5));
        var loft = await Call<FeatureLoftRequest, FeatureResult>(new FeatureLoftRequest { Document = path, Profiles = new List<string> { big.Sketch.Name, small.Sketch.Name } });
        Near(10.0 / 3 * (400 + 100 + 200), await VolumeOf(path), "the frustum's volume (h/3 × (A1 + A2 + √(A1 A2)))");
        return $"{loft.Feature.Name}: {string.Join("; ", loft.Checks)}";
    });

    [SkippableFact, Step(24)]
    public Task A_shell_hollows_the_block_exactly_and_a_rib_stands_in_it() => _run.CaseAsync(24, "feature.shell: the block open at the top with 2 mm walls, 3392 mm³; feature.rib across it", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("shell");
        string path = block.Path!;
        var top = await At(path, TopologyKind.Face, new Point3(20, 10, 10));
        var shell = await Call<FeatureShellRequest, FeatureResult>(new FeatureShellRequest { Document = path, FacesToRemove = new List<string> { top.Reference! }, ThicknessMm = 2 });
        Near(8000 - 36 * 16 * 8, await VolumeOf(path), "the shelled block's volume (8000 less a 36 x 16 x 8 cavity)");
        Assert.Contains(shell.Checks, c => c.Contains("are gone"));

        // A 2 mm rib across the cavity: a line along y at x 20 on the plane z 6, run normal to its sketch down to the
        // floor (z 2), between the side walls (y 2 to 18). Which side the material goes is SOLIDWORKS' to say: flipped if
        // the first way meets nothing.
        var level = await PlaneAt(path, "front", 6, p => p.Z);
        var line = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = level,
            Lines = new List<SketchLine> { new SketchLine { Id = "rib", Start = new Point2(20, 4), End = new Point2(20, 16) } },
        });
        double before = await VolumeOf(path);
        bool flipped = false;
        FeatureResult rib;
        try
        {
            rib = await Call<FeatureRibRequest, FeatureResult>(new FeatureRibRequest { Document = path, Sketch = line.Sketch.Name, ThicknessMm = 2, NormalToSketch = true });
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.OperationFailed || e.Code == ErrorCode.VerificationFailed)
        {
            flipped = true;
            rib = await Call<FeatureRibRequest, FeatureResult>(new FeatureRibRequest { Document = path, Sketch = line.Sketch.Name, ThicknessMm = 2, NormalToSketch = true, FlipMaterial = true });
        }
        double added = await VolumeOf(path) - before;
        Assert.True(added > 0 && added <= 2 * 16 * 8 + 1e-6, $"the rib added {added} mm³; a 2 mm rib inside a 16 x 8 mm cavity adds more than 0 and at most 256 mm³");
        return $"{shell.Feature.Name}: {string.Join("; ", shell.Checks)}; {rib.Feature.Name} added {added:0.###} mm³ (2 × 16 × 4 = 128 if it met the floor and both walls){(flipped ? ", with flip_material" : "")}";
    });

    [SkippableFact, Step(25)]
    public Task A_draft_tilts_a_side_by_its_angle() => _run.CaseAsync(25, "feature.draft: the block's x = 40 side drafted 5° about the front plane, 87.489 mm³ either way", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("draft");
        string path = block.Path!;
        var side = await At(path, TopologyKind.Face, new Point3(40, 10, 5));
        var draft = await Call<FeatureDraftRequest, FeatureResult>(new FeatureDraftRequest { Document = path, NeutralPlane = "front", Faces = new List<string> { side.Reference! }, AngleDeg = 5 });
        double change = await VolumeOf(path) - 8000;
        // A 10 mm tall, 20 mm wide face tilted 5° about its bottom edge sweeps a wedge of ½ × 10 × 10 tan 5° × 20.
        Near(0.5 * 10 * 10 * Math.Tan(5 * Math.PI / 180) * 20, Math.Abs(change), "the draft's wedge");
        Assert.Contains(draft.Checks, c => c.Contains("stand at 85°"));
        return $"{draft.Feature.Name}: {string.Join("; ", draft.Checks)}; the volume {(change > 0 ? "grew" : "shrank")} by {Math.Abs(change):0.###} mm³";
    });

    [SkippableFact, Step(26)]
    public Task Hole_wizard_holes_land_on_their_positions() => _run.CaseAsync(26, "feature.hole: two ISO Ø5.0 simple holes through the block, 392.699 mm³, and an ISO M5 counterbore", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("holes");
        string path = block.Path!;
        var top = await At(path, TopologyKind.Face, new Point3(20, 10, 10));
        var simple = await Call<FeatureHoleRequest, FeatureResult>(new FeatureHoleRequest
        {
            Document = path,
            Face = top.Reference!,
            Positions = new List<Point3> { new Point3(8, 10, 10), new Point3(32, 10, 10) },
            Size = "Ø5.0",
            End = HoleEnd.ThroughAll,
        });
        Near(8000 - 2 * Math.PI * 6.25 * 10, await VolumeOf(path), "the volume after two 5 mm holes through 10 mm");
        double before = await VolumeOf(path);
        var top2 = await At(path, TopologyKind.Face, new Point3(20, 10, 10));
        var cbore = await Call<FeatureHoleRequest, FeatureResult>(new FeatureHoleRequest
        {
            Document = path,
            Face = top2.Reference!,
            Positions = new List<Point3> { new Point3(20, 10, 10) },
            Type = HoleType.Counterbore,
            Size = "M5",
            End = HoleEnd.ThroughAll,
        });
        Assert.True(await VolumeOf(path) < before, "the counterbore took material");
        return $"{simple.Feature.Name}: {string.Join("; ", simple.Checks)}; {cbore.Feature.Name}: {string.Join("; ", cbore.Checks)}, " +
               $"its cylinders {string.Join(", ", cbore.Faces.Where(f => f.Geometry == GeometryType.Cylinder).Select(f => $"r {f.RadiusMm:0.###}"))}";
    });

    [SkippableFact, Step(27)]
    public Task Patterns_repeat_a_hole_by_its_exact_volume() => _run.CaseAsync(27, "feature.linear_pattern: a 2.5 mm hole three times along x, and its bore as a face pattern; feature.circular_pattern: a 1 mm hole four times round a 3 mm one", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("linear-pattern");
        string path = block.Path!;
        // From the middle, 8 mm apart: the instances land inside the block whichever way the edge runs.
        string seed = await Through(path, 20, 10, 2.5);
        var edge = await At(path, TopologyKind.Edge, new Point3(20, 0, 0));
        var linear = await Call<FeatureLinearPatternRequest, FeatureResult>(new FeatureLinearPatternRequest
        {
            Document = path, Features = new List<string> { seed }, Direction = edge.Reference!, SpacingMm = 8, Count = 3,
        });
        Near(8000 - 3 * Math.PI * 6.25 * 10, await VolumeOf(path), "three 2.5 mm holes");

        var round = await Block("circular-pattern");
        string path2 = round.Path!;
        await Through(path2, 20, 10, 3);
        string small = await Through(path2, 26, 10, 1);
        var bore = await At(path2, TopologyKind.Face, new Point3(23, 10, 5), GeometryType.Cylinder);
        var circular = await Call<FeatureCircularPatternRequest, FeatureResult>(new FeatureCircularPatternRequest
        {
            Document = path2, Features = new List<string> { small }, Axis = bore.Reference!, Count = 4,
        });
        Near(8000 - Math.PI * 9 * 10 - 4 * Math.PI * 1 * 10, await VolumeOf(path2), "a 3 mm hole and four 1 mm holes round it");

        // A face pattern (geometry pattern): the bore of a 2.5 mm hole repeated along x. The operation checks the instances and
        // that the volume moved; the exact value three holes would give is reported, not required.
        var faces = await Block("face-pattern");
        string path3 = faces.Path!;
        await Through(path3, 20, 10, 2.5);
        double holed = await VolumeOf(path3);
        var bore3 = await At(path3, TopologyKind.Face, new Point3(22.5, 10, 5), GeometryType.Cylinder);
        var edge3 = await At(path3, TopologyKind.Edge, new Point3(20, 0, 0));
        var facePattern = await Call<FeatureLinearPatternRequest, FeatureResult>(new FeatureLinearPatternRequest
        {
            Document = path3, Faces = new List<string> { bore3.Reference! }, Direction = edge3.Reference!, SpacingMm = 8, Count = 3,
        });
        double afterFaces = await VolumeOf(path3);
        Assert.True(afterFaces < holed, $"the face pattern left the volume at {afterFaces} mm³, not less than {holed}");
        return $"{linear.Feature.Name}: {string.Join("; ", linear.Checks)}; {circular.Feature.Name}: {string.Join("; ", circular.Checks)}; " +
               $"face pattern {facePattern.Feature.Name}: {string.Join("; ", facePattern.Checks)}, {afterFaces:0.###} mm³ (three whole holes would be {8000 - 3 * Math.PI * 6.25 * 10:0.###})";
    });

    [SkippableFact, Step(28)]
    public Task A_mirror_repeats_a_hole_and_a_mirrored_body_doubles_the_block() => _run.CaseAsync(28, "feature.mirror: a 2 mm hole about x = 20, then the whole block about the right plane, 16000 mm³", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("mirror");
        string path = block.Path!;
        string seed = await Through(path, 10, 10, 2);
        var mid = await PlaneAt(path, "right", 20, p => p.X);
        var mirror = await Call<FeatureMirrorRequest, FeatureResult>(new FeatureMirrorRequest { Document = path, Features = new List<string> { seed }, Plane = mid });
        Near(8000 - 2 * Math.PI * 4 * 10, await VolumeOf(path), "the hole and its mirror");
        var whole = await Block("mirror-body");
        var body = await Call<FeatureMirrorRequest, FeatureResult>(new FeatureMirrorRequest { Document = whole.Path!, Body = true, Plane = "right" });
        Near(16000, await VolumeOf(whole.Path!), "the block mirrored about its x = 0 face");
        var box = await BoxOf(whole.Path!);
        Near(-40, box.Min.X, "the mirrored block's far side");
        return $"{mirror.Feature.Name}: {string.Join("; ", mirror.Checks)}; {body.Feature.Name}: {string.Join("; ", body.Checks)}, bodies {body.Change.BodiesAfter}";
    });

    [SkippableFact, Step(29)]
    public Task Configurations_hold_their_own_suppression() => _run.CaseAsync(29, "configuration.add, configuration.activate and feature.suppress in one of them: the hole is in one configuration and not the other", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("configurations");
        string path = block.Path!;
        string hole = await Through(path, 20, 10, 5);
        double holed = 8000 - Math.PI * 25 * 10;
        var list = await Call<ConfigurationListRequest, ConfigurationListResult>(new ConfigurationListRequest { Document = path });
        string original = list.Active;
        var added = await Call<ConfigurationAddRequest, ConfigurationResult>(new ConfigurationAddRequest { Document = path, Name = "solid" });
        Assert.Equal("solid", added.Active);
        await Call<FeatureSuppressRequest, FeatureSuppressResult>(new FeatureSuppressRequest { Document = path, Features = new List<string> { hole } });
        Near(8000, await VolumeOf(path), "the block with the hole suppressed in 'solid'");
        var back = await Call<ConfigurationActivateRequest, ConfigurationResult>(new ConfigurationActivateRequest { Document = path, Name = original });
        Near(holed, back.Change.VolumeAfterMm3, $"the block in '{original}', where the hole is not suppressed");
        var again = await Call<ConfigurationActivateRequest, ConfigurationResult>(new ConfigurationActivateRequest { Document = path, Name = "solid" });
        Near(8000, again.Change.VolumeAfterMm3, "the block in 'solid' again");
        var refused = await Refused(() => Call<ConfigurationAddRequest, ConfigurationResult>(new ConfigurationAddRequest { Document = path, Name = "solid" }));
        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        await Call<ConfigurationActivateRequest, ConfigurationResult>(new ConfigurationActivateRequest { Document = path, Name = original });
        return $"configurations [{string.Join(", ", again.Configurations)}], '{original}' first; 'solid' {again.Change.VolumeAfterMm3:0.###} mm³, '{original}' {back.Change.VolumeAfterMm3:0.###} mm³; adding 'solid' again: {refused.Message}";
    });

    /// <summary>
    /// The 3.C lesson that SOLIDWORKS makes features one might expect it to refuse: what it does with a shell thicker than
    /// the block, a hole of a size the table lacks and a pattern that runs off the part, each reported as found. Whatever
    /// it does, the part must end as it was or as the checks passed it: never half-made.
    /// </summary>
    [SkippableFact, Step(30)]
    public Task What_SOLIDWORKS_will_not_make_leaves_the_part_as_it_was() => _run.CaseAsync(30, "a 12 mm shell of a 10 mm block, an M99 hole and a pattern off the part: each refused and rolled back, or made and checked", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("refusals");
        string path = block.Path!;
        var found = new List<string>();
        async Task Try(string what, Func<Task> call)
        {
            double before = await VolumeOf(path);
            var tree = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Count;
            try
            {
                await call();
                found.Add($"{what}: made, and its checks passed ({await VolumeOf(path):0.###} mm³)");
            }
            catch (SwAgentException open) when (open.Code == ErrorCode.DialogOpen)
            {
                // SOLIDWORKS raised a prompt mid-feature: read it, never touch a sign-in window, close any other and read
                // the outcome before measuring, so nothing is read while a dialog is up.
                var dialog = (await _run.AddinStateAsync()).Dialog ?? throw new InvalidOperationException($"{what} answered dialog_open, and no dialog is open");
                if (dialog.NeedsPerson)
                {
                    _run.Say($"STOP: SOLIDWORKS is showing '{dialog.Title}', a sign-in or licence window; only a person answers it. Stop the run and report");
                    throw new InvalidOperationException($"'{dialog.Title}' needs a person; the case stopped without touching it");
                }
                var answer = await Call<DialogRespondRequest, DialogRespondResult>(new DialogRespondRequest { Dialog = dialog.Id, Button = "close", WaitMs = 30000 });
                Assert.NotEqual(DialogOutcome.DialogOpen, answer.Outcome);
                Assert.NotEqual(DialogOutcome.StillRunning, answer.Outcome);
                if (answer.Outcome == DialogOutcome.Error)
                {
                    Near(before, await VolumeOf(path), $"the volume after {what} was rolled back");
                    Assert.Equal(tree, (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Count);
                }
                found.Add($"{what}: SOLIDWORKS asked '{dialog.Title}' ({string.Join(" | ", dialog.Text)}; buttons {string.Join(", ", dialog.Buttons.Select(b => b.Text))}), closed it; " +
                          $"outcome {WireNames.ForEnum(answer.Outcome)}{(answer.Error != null ? $" ({answer.Error.Message})" : "")}, {await VolumeOf(path):0.###} mm³");
            }
            catch (SwAgentException e)
            {
                Near(before, await VolumeOf(path), $"the volume after {what} was refused");
                Assert.Equal(tree, (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Count);
                found.Add($"{what}: {WireNames.ForEnum(e.Code)} ({e.Message})");
            }
        }
        var top = await At(path, TopologyKind.Face, new Point3(20, 10, 10));
        await Try("a 12 mm shell", () => Call<FeatureShellRequest, FeatureResult>(new FeatureShellRequest { Document = path, FacesToRemove = new List<string> { top.Reference! }, ThicknessMm = 12 }));
        var face = await At(path, TopologyKind.Face, new Point3(20, 10, 10));
        await Try("an ISO M99 tapped hole", () => Call<FeatureHoleRequest, FeatureResult>(new FeatureHoleRequest { Document = path, Face = face.Reference!, Positions = new List<Point3> { new Point3(20, 10, 10) }, Type = HoleType.Tapped, Size = "M99", End = HoleEnd.ThroughAll }));
        // 25 mm from the middle is off the 40 mm block whichever way the edge runs.
        string seed = await Through(path, 20, 10, 2);
        var edge = await At(path, TopologyKind.Edge, new Point3(20, 0, 0));
        await Try("a linear pattern running off the part", () => Call<FeatureLinearPatternRequest, FeatureResult>(new FeatureLinearPatternRequest { Document = path, Features = new List<string> { seed }, Direction = edge.Reference!, SpacingMm = 25, Count = 2 }));
        return string.Join("; ", found);
    });
}
