using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.Live.Tests;

/// <summary>
/// The fault suite's model-change cases (design 371, Testing, Fault suite additions; plan 3.C): a stale reference is
/// reported and never substituted, and a model change waits while a sketch is open for editing. Both run with nobody at
/// SOLIDWORKS: the suite makes its own parts in its scratch directory, opens the sketch itself (diag.edit_sketch), and
/// closes what it made. Numbered after the cases that end SOLIDWORKS (9 to 12), which a live run does not select.
/// </summary>
public sealed partial class FaultSuite
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(120);

    /// <summary>A 40 x 20 x 10 mm block (Boss-Extrude1 on Sketch1) in the run's scratch directory, recorded as the run's.</summary>
    async Task<string> BlockAsync(string name)
    {
        string path = _run.Guard.NewScratchPath(name);
        _run.Guard.Created(path);
        await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = path, XMm = 40, YMm = 20, ZMm = 10 }, Long);
        return path;
    }

    Task CloseAsync(string path) => Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path, Discard = true }, Long);

    /// <summary>
    /// Design 371's stale reference: "a reference to a face, then an edit that removes that face (the sketch dimension
    /// changed so the feature no longer makes it)". A 5 mm blind pocket from the bottom of the 10 mm block has a floor at
    /// z 5; its depth set to 15 mm takes it through the block, so the floor is gone, with no rebuild error. The floor's
    /// reference must then answer reference_unresolved, never resolving to the nearest face left.
    /// </summary>
    [SkippableFact, Step(13)]
    public Task A_stale_reference_is_reported_and_never_substituted() => _run.CaseAsync(13, "a pocket's floor removed by deepening it through the block (dimension.set): its reference answers reference_unresolved, and nothing changed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = await BlockAsync("stale");
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "pocket", Center = new Point2(20, 10), RadiusMm = 5 } },
        }, Long);
        var pocket = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = sketch.Sketch.Name, Mode = FeatureMode.Cut, DepthMm = 5 }, Long);
        var floor = pocket.Faces.Single(f => f.Geometry == GeometryType.Plane);
        Assert.True(Math.Abs(floor.Origin!.Z - 5) < 1e-6, $"the pocket's floor is at z {floor.Origin.Z}, not 5");
        var depth = (await Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = path, Feature = pocket.Feature.Name }, Long))
            .Dimensions.Single(d => Math.Abs(d.Value - 5) < 1e-9);
        var deeper = await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = path, Dimension = depth.Name, Value = 15 }, Long);
        Assert.Equal(0, deeper.Change.Rebuild.Errors);
        double through = 8000 - Math.PI * 25 * 10;
        Assert.True(Math.Abs(deeper.Change.VolumeAfterMm3 - through) < 1e-6 * through, $"volume {deeper.Change.VolumeAfterMm3} mm³ with the pocket through the block, not {through}");
        var before = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path }, Long);

        var stale = await Assert.ThrowsAsync<SwAgentException>(() =>
            Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Offset, From = floor.Reference!, DistanceMm = 5 }, Long));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
        string lastWas = (string)stale.Error.Details!["last_was"]!;
        Assert.Contains(pocket.Feature.Name!, lastWas);
        var after = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path }, Long);
        Assert.Equal(before.Features.Select(f => f.Name), after.Features.Select(f => f.Name));
        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = path }, Long);
        Assert.True(Math.Abs(mass.VolumeMm3 - through) < 1e-6 * through, $"volume {mass.VolumeMm3} mm³ after the refused call, not {through}");
        await CloseAsync(path);
        return $"{pocket.Feature.Name}'s floor at z 5 ({floor.Reference!.Length} characters); {depth.Name} 5 -> 15 mm, rebuild errors 0, the floor gone; " +
               $"{stale.Message} (reason {stale.Error.Details["reason"]}); tree unchanged ({after.Features.Count} features), volume {mass.VolumeMm3:0.###} mm³";
    });

    [SkippableFact, Step(14)]
    public Task A_change_waits_while_a_sketch_is_open_for_editing() => _run.CaseAsync(14, "busy with user_editing while a sketch is open for editing (diag.edit_sketch), then the change works", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = await BlockAsync("editing");
        var plane = new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Offset, From = "front", DistanceMm = 5 };
        var open = await Call<DiagEditSketchRequest, DiagEditingResult>(new DiagEditSketchRequest { Document = path, Sketch = "Sketch1", Open = true }, Long);
        SwAgentException busy;
        try
        {
            Assert.Equal("Sketch1", open.Sketch);
            busy = await Assert.ThrowsAsync<SwAgentException>(() => Call<RefgeomPlaneRequest, FeatureResult>(plane, Long));
        }
        finally
        {
            await Call<DiagEditSketchRequest, DiagEditingResult>(new DiagEditSketchRequest { Document = path, Open = false }, Long);
        }
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("user_editing", busy.Error.Details!["reason"]);
        Assert.Equal("before", busy.Error.Details["detected"]);
        Assert.Equal("Sketch1", busy.Error.Details["sketch"]);
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path }, Long);
        Assert.Equal(3, tree.Features.Count(f => f.Type == "RefPlane"));
        var made = await Call<RefgeomPlaneRequest, FeatureResult>(plane, Long);
        await CloseAsync(path);
        return $"busy ({busy.Message}); after the sketch was exited, {made.Feature.Name} was made ({string.Join("; ", made.Checks)})";
    });
}
