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

/// <summary>The parts suite's run: its own id (…-parts-suite), directory, scratch directory and report.</summary>
public sealed class PartsSuiteRun : LiveRun
{
    public PartsSuiteRun()
        : base("parts-suite")
    {
    }
}

/// <summary>
/// The parts live suite (design 371, Testing; plan 3.A covers documents, references, inspection and selection, plan
/// 3.B sketches, dimensions, rollback and the user_editing precondition, plan 3.C features, reference geometry, suppress, delete and measure):
/// every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly as swmcp serve does, in [Step] order, and
/// checks geometry against exact values. It never ends SOLIDWORKS. Before every case it stops the run if a document it
/// did not create is open; it creates documents only in its scratch directory, records each before creating it, and
/// closes them all at the end. Skipped unless SWMCP_LIVE=1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class PartsSuite : IClassFixture<PartsSuiteRun>
{
    static readonly TimeSpan Short = TimeSpan.FromSeconds(30);
    const double Tight = 1e-6;

    readonly LiveRun _run;

    public PartsSuite(PartsSuiteRun run)
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

    Task<DocumentInfo> Block(string name) =>
        Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = NewPart(name), XMm = 40, YMm = 20, ZMm = 10 });

    static void Near(double expected, double actual, string what, double tolerance = Tight) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)), $"{what}: expected {expected}, got {actual}");

    static void Parallel(Vector3 expected, Vector3 actual, string what) =>
        Assert.True(Math.Abs(expected.X * actual.X + expected.Y * actual.Y + expected.Z * actual.Z) > 1 - 1e-9, $"{what}: expected along {expected}, got {actual}");

    static async Task<SwAgentException> Refused(Func<Task> call) => await Assert.ThrowsAsync<SwAgentException>(call);

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
        try
        {
            await Call<DiagSleepRequest, DiagSleepResult>(new DiagSleepRequest { Ms = 1 });
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.InvalidArgument)
        {
            throw _run.Stop("diagnostic operations are off in this add-in load, although addin.state said they were on; rerun, and report it: " + e.Message);
        }
        _run.SwRevision = status.SwRevision;
        _run.AddinVersion = status.AddinVersion;
        Assert.Equal(Protocol.Version, status.ProtocolVersion);
        return $"SOLIDWORKS {status.SwRevision}, add-in {status.AddinVersion}, protocol {status.ProtocolVersion}; documents open: {state.Documents.Count}";
    });

    [SkippableFact, Step(2)]
    public Task Documents_are_created_saved_closed_and_opened() => _run.CaseAsync(2, "document.new, save, save_as, close, open", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string first = NewPart("doc-new");
        var made = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = first });
        Assert.Equal(first, made.Document.Path, ignoreCase: true);
        Assert.False(made.Document.Dirty);
        Assert.True(File.Exists(first));

        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = first });
        var planes = tree.Features.Where(f => f.Kind == ReferenceKind.Plane).ToList();
        Assert.True(planes.Count >= 3, $"the template has {planes.Count} planes");
        Assert.Contains(tree.Features, f => f.Type == "OriginProfileFeature");

        var saved = await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = first });
        Assert.True(saved.Bytes > 0);

        string second = NewPart("doc-renamed");
        var renamed = await Call<DocumentSaveAsRequest, DocumentSaveResult>(new DocumentSaveAsRequest { Document = first, Path = second });
        Assert.Equal(second, renamed.Document.Path, ignoreCase: true);
        Assert.True(File.Exists(second));

        var overwrite = await Refused(() => Call<DocumentSaveAsRequest, DocumentSaveResult>(new DocumentSaveAsRequest { Document = second, Path = first }));
        Assert.Equal(ErrorCode.InvalidArgument, overwrite.Code);

        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = second });
        Assert.DoesNotContain((await _run.AddinStateAsync()).Documents, d => string.Equals(d.Path, second, StringComparison.OrdinalIgnoreCase));

        var opened = await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = second });
        Assert.False(opened.AlreadyOpen);
        var again = await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = second });
        Assert.True(again.AlreadyOpen);
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = second });
        return $"new at {Path.GetFileName(first)} with {planes.Count} planes and the origin; saved ({saved.Bytes} bytes); saved as {Path.GetFileName(second)}; " +
               $"overwrite refused ({WireNames.ForEnum(overwrite.Code)}); closed; opened (warnings: {string.Join(", ", opened.Warnings.DefaultIfEmpty("none"))}); opened again: already_open; closed";
    });

    [SkippableFact, Step(3)]
    public Task Close_refuses_what_is_not_the_agents_and_unsaved_work() => _run.CaseAsync(3, "document.close refuses the user's document and unsaved changes", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("close-guard");
        var part = await Call<DiagScratchPartRequest, DocumentInfo>(new DiagScratchPartRequest { Path = path });
        Assert.True(part.Dirty);

        var notOurs = await Refused(() => Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path, Discard = true }));
        Assert.Equal("users_document", notOurs.Error.Details!["field"]);
        var unsaved = await Refused(() => Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path, UsersDocument = true }));
        Assert.Equal("discard", unsaved.Error.Details!["field"]);
        Assert.Contains((await _run.AddinStateAsync()).Documents, d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

        var closed = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path, Discard = true, UsersDocument = true });
        Assert.True(closed.Discarded);
        return "a part the client did not open or create: refused without users_document; refused with unsaved changes without discard; closed with both";
    });

    [SkippableFact, Step(4)]
    public Task Standard_planes_resolve_by_position_and_selection_is_reported() => _run.CaseAsync(4, "front, top, right by position; selection.get and selection.changed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("planes");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        long cursor = Sw.Events.LastSeq;

        var selected = await Call<DiagSelectRequest, SelectionResult>(new DiagSelectRequest { Document = path, Items = new List<string> { "front", "top", "right" } });
        Assert.Equal(new[] { "plane", "plane", "plane" }, selected.Items.Select(i => i.Type));
        Parallel(new Vector3(0, 0, 1), selected.Items[0].Entity!.Normal!, "front");
        Parallel(new Vector3(0, 1, 0), selected.Items[1].Entity!.Normal!, "top");
        Parallel(new Vector3(1, 0, 0), selected.Items[2].Entity!.Normal!, "right");

        var got = await Call<SelectionGetRequest, SelectionResult>(new SelectionGetRequest { Document = path });
        Assert.Equal(selected.Items.Select(i => i.Entity!.Name), got.Items.Select(i => i.Entity!.Name));

        EventEnvelope? changed = null;
        await LiveRun.WaitForAsync(() =>
        {
            changed = Sw.ReadEvents(new SwEventsRequest { AfterSeq = cursor, Kinds = new List<string> { "selection.changed" } }).Events.LastOrDefault();
            return Task.FromResult(changed != null && ((List<object?>)changed.Data["items"]!).Count == 3);
        }, TimeSpan.FromSeconds(10), "selection.changed with the three planes");

        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path, Discard = true });
        return $"front {selected.Items[0].Entity!.Name} {selected.Items[0].Entity!.Normal}, top {selected.Items[1].Entity!.Name} {selected.Items[1].Entity!.Normal}, " +
               $"right {selected.Items[2].Entity!.Name} {selected.Items[2].Entity!.Normal}; selection.get agreed; selection.changed raised with 3 items";
    });

    [SkippableFact, Step(5)]
    public Task A_block_measures_exactly() => _run.CaseAsync(5, "model.mass, model.bounding_box, model.tree and model.rebuild_status on a 40 x 20 x 10 mm block", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("measure");
        string doc = block.Path!;
        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = doc });
        Assert.Equal(1, mass.Bodies);
        Near(8000, mass.VolumeMm3, "volume, mm³");
        Near(2800, mass.AreaMm2, "area, mm²");
        Near(20, mass.CenterOfMass!.X, "centre of mass X");
        Near(10, mass.CenterOfMass.Y, "centre of mass Y");
        Near(5, mass.CenterOfMass.Z, "centre of mass Z");

        var exact = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = doc, Precise = true });
        Near(40, exact.SizeX, "size X");
        Near(20, exact.SizeY, "size Y");
        Near(10, exact.SizeZ, "size Z");
        Near(0, exact.Box!.Min.X, "min X");
        Near(10, exact.Box.Max.Z, "max Z");
        var quick = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = doc });
        Assert.True(quick.SizeX >= 40 - Tight && quick.SizeY >= 20 - Tight && quick.SizeZ >= 10 - Tight, "the quick box is smaller than the block");

        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = doc });
        var extrude = tree.Features.Single(f => f.Type == "Extrusion");
        Assert.Equal("RefPlane", tree.Features[0].Type);
        Assert.Equal(extrude.Name, Assert.Single(tree.Features, f => f.Type == "ProfileFeature").Parent);
        var status = await Call<ModelRebuildStatusRequest, RebuildResult>(new ModelRebuildStatusRequest { Document = doc });
        Assert.Equal(0, status.Errors);
        return $"volume {mass.VolumeMm3} mm³, area {mass.AreaMm2} mm², centre {mass.CenterOfMass}, density {mass.DensityKgM3} kg/m³ ({mass.Material ?? "no material"}); " +
               $"exact box {exact.SizeX} x {exact.SizeY} x {exact.SizeZ}, quick {quick.SizeX:0.###} x {quick.SizeY:0.###} x {quick.SizeZ:0.###}; " +
               $"tree: {string.Join(", ", tree.Features.Select(f => f.Name))}; rebuild errors 0";
    });

    [SkippableFact, Step(6)]
    public Task Model_find_names_the_blocks_faces_edges_and_vertices() => _run.CaseAsync(6, "model.find on the block", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string doc = LiveScratchPath("measure");
        Task<ModelFindResult> Find(ModelFindRequest r)
        {
            r.Document = doc;
            return Call<ModelFindRequest, ModelFindResult>(r);
        }

        var faces = await Find(new ModelFindRequest { Kind = TopologyKind.Face });
        Assert.Equal(6, faces.Total);
        Assert.All(faces.Entities, f => Assert.Equal(GeometryType.Plane, f.Geometry));
        Assert.All(faces.Entities, f => Assert.StartsWith("r1.face.", f.Reference));

        var top = Assert.Single((await Find(new ModelFindRequest { Kind = TopologyKind.Face, Direction = new Vector3(0, 0, 1) })).Entities);
        Near(800, top.AreaMm2!.Value, "top face area");
        Near(10, top.Origin!.Z, "top face Z");
        var bottom = Assert.Single((await Find(new ModelFindRequest { Kind = TopologyKind.Face, Direction = new Vector3(0, 0, -1) })).Entities);
        Near(0, bottom.Origin!.Z, "bottom face Z");

        string extrude = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = doc })).Features.Single(f => f.Type == "Extrusion").Name;
        Assert.Equal(6, (await Find(new ModelFindRequest { Kind = TopologyKind.Face, Feature = extrude })).Total);

        var edges = await Find(new ModelFindRequest { Kind = TopologyKind.Edge });
        Assert.Equal(12, edges.Total);
        var alongX = await Find(new ModelFindRequest { Kind = TopologyKind.Edge, Direction = new Vector3(1, 0, 0) });
        Assert.Equal(4, alongX.Total);
        Assert.All(alongX.Entities, e => Near(40, e.LengthMm!.Value, "edge along X"));

        Assert.Equal(8, (await Find(new ModelFindRequest { Kind = TopologyKind.Vertex })).Total);
        var corner = Assert.Single((await Find(new ModelFindRequest { Kind = TopologyKind.Vertex, Near = new Point3(40, 20, 10), MaxDistanceMm = 0.001 })).Entities);
        Near(0, corner.DistanceMm!.Value, "corner distance", 1e-6);
        var onTop = (await Find(new ModelFindRequest { Kind = TopologyKind.Face, Near = new Point3(20, 10, 10) })).Entities;
        Assert.Single(onTop);
        Assert.Equal(top.Reference, onTop[0].Reference);
        return $"6 plane faces; top {top.AreaMm2} mm² at z {top.Origin.Z}; bottom at z {bottom.Origin.Z}; {extrude} made 6; 12 edges, 4 along X of 40 mm; " +
               $"8 vertices, the corner (40, 20, 10) at {corner.DistanceMm} mm; the point (20, 10, 10) finds the top face only";
    });

    [SkippableFact, Step(7)]
    public Task References_survive_rebuild_rename_and_reopen() => _run.CaseAsync(7, "references survive a rebuild, a save as and a close and reopen; never cross documents", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("references");
        string doc = block.Path!;
        var top = (await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = doc, Kind = TopologyKind.Face, Direction = new Vector3(0, 0, 1) })).Entities.Single();
        string reference = top.Reference!;

        async Task<double> AreaVia(string document)
        {
            var selected = await Call<DiagSelectRequest, SelectionResult>(new DiagSelectRequest { Document = document, Items = new List<string> { reference } });
            return Assert.Single(selected.Items).Entity!.AreaMm2!.Value;
        }

        Near(800, await AreaVia(doc), "after issue");
        await Call<DocumentRebuildRequest, RebuildResult>(new DocumentRebuildRequest { Document = doc, Force = true });
        Near(800, await AreaVia(doc), "after a forced rebuild");

        string renamed = NewPart("references-renamed");
        await Call<DocumentSaveAsRequest, DocumentSaveResult>(new DocumentSaveAsRequest { Document = doc, Path = renamed });
        Near(800, await AreaVia(renamed), "after save as");

        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = renamed });
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = renamed });
        Near(800, await AreaVia(renamed), "after close and reopen");

        var other = await Block("references-other");
        var crossed = await Refused(() => Call<DiagSelectRequest, SelectionResult>(new DiagSelectRequest { Document = other.Path, Items = new List<string> { reference } }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, crossed.Code);
        Assert.Equal("other_document", crossed.Error.Details!["reason"]);
        return $"the top face ({top.Reference!.Length} characters) resolved after issue, a forced rebuild, a save as and a close and reopen; " +
               $"in another part: reference_unresolved ({crossed.Error.Details["reason"]}), last was '{crossed.Error.Details["last_was"]}'";
    });

    static List<string> Of(params string[] entities) => entities.ToList();

    /// <summary>The 40 x 20 plate: four lines tied at their ends and to the origin, a 5 mm hole at (20, 10), all dimensioned, so fully defined.</summary>
    static SketchCreateRequest Plate(string document) => new SketchCreateRequest
    {
        Document = document,
        On = "front",
        Name = "plate",
        Lines = new List<SketchLine>
        {
            new SketchLine { Id = "l1", Start = new Point2(0, 0), End = new Point2(40, 0) },
            new SketchLine { Id = "l2", Start = new Point2(40, 0), End = new Point2(40, 20) },
            new SketchLine { Id = "l3", Start = new Point2(40, 20), End = new Point2(0, 20) },
            new SketchLine { Id = "l4", Start = new Point2(0, 20), End = new Point2(0, 0) },
        },
        Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = new Point2(20, 10), RadiusMm = 2.5 } },
        Relations = new List<SketchRelationItem>
        {
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = Of("l1.end", "l2.start") },
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = Of("l2.end", "l3.start") },
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = Of("l3.end", "l4.start") },
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = Of("l4.end", "l1.start") },
            new SketchRelationItem { Type = SketchRelationType.Horizontal, Entities = Of("l1") },
            new SketchRelationItem { Type = SketchRelationType.Horizontal, Entities = Of("l3") },
            new SketchRelationItem { Type = SketchRelationType.Vertical, Entities = Of("l2") },
            new SketchRelationItem { Type = SketchRelationType.Vertical, Entities = Of("l4") },
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = Of("l1.start", "origin") },
        },
        Dimensions = new List<SketchDimensionItem>
        {
            new SketchDimensionItem { Name = "width", Entities = Of("l1"), Value = 40 },
            new SketchDimensionItem { Name = "height", Entities = Of("l2"), Value = 20 },
            new SketchDimensionItem { Name = "hole_d", Entities = Of("hole"), Value = 5 },
            new SketchDimensionItem { Name = "hole_x", Entities = Of("origin", "hole.center"), Value = 20, Kind = DimensionKind.Horizontal },
            new SketchDimensionItem { Name = "hole_y", Entities = Of("origin", "hole.center"), Value = 10, Kind = DimensionKind.Vertical },
        },
    };

    static double Dimension(SketchReadResult read, string name) => read.Dimensions.Single(d => d.Name.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase)).Value;

    async Task<int> SketchCount(string document) =>
        (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = document })).Features.Count(f => f.Type == "ProfileFeature");

    [SkippableFact, Step(8)]
    public Task A_fully_defined_sketch_is_drawn_in_one_call() => _run.CaseAsync(8, "sketch.create: a fully defined plate on the front plane, read back", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("sketches");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var made = await Call<SketchCreateRequest, SketchCreateResult>(Plate(path));
        Assert.Equal("plate", made.Sketch.Name);
        Assert.Equal(SolveState.FullyDefined, made.Sketch.Solve);
        Parallel(new Vector3(0, 0, 1), made.Sketch.Normal, "the plate's normal");
        Near(0, made.Sketch.Origin.Z, "the plate's origin Z");
        Assert.Equal("plate", Assert.Single(made.Change.Created).Name);
        Assert.Equal(new[] { 40.0, 20, 5, 20, 10 }, made.Dimensions.Select(d => Math.Round(d.Value, 9)));

        var read = await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "plate" });
        Assert.Equal(4, read.Segments.Count(s => s.Kind == SketchSegmentKind.Line && !s.Construction));
        var hole = Assert.Single(read.Segments, s => s.Kind == SketchSegmentKind.Circle);
        Near(2.5, hole.RadiusMm!.Value, "hole radius");
        Near(20, hole.Center!.X, "hole x");
        Near(10, hole.Center.Y, "hole y");
        Assert.Contains(made.Ids.Single(i => i.Id == "hole").References[0], read.Segments.Select(s => s.Reference));
        Near(40, Dimension(read, "width"), "width");
        return $"plate: {made.Sketch.Solve}, normal {made.Sketch.Normal}, origin {made.Sketch.Origin}; read back {read.Segments.Count} segments, {read.Relations.Count} relations " +
               $"({string.Join(", ", read.Relations.Select(r => r.Type).Distinct())}), dimensions {string.Join(", ", read.Dimensions.Select(d => $"{d.Name}={d.Value:0.###}"))}";
    });

    [SkippableFact, Step(9)]
    public Task Every_entity_kind_draws_and_a_face_takes_a_sketch() => _run.CaseAsync(9, "sketch.create: every entity kind on the top plane, and a sketch on the block's top face", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = LiveScratchPath("sketches");
        var kinds = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "top",
            Name = "kinds",
            Lines = new List<SketchLine> { new SketchLine { Id = "line", Start = new Point2(0, 0), End = new Point2(10, 0) } },
            CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = new Point2(0, -20), End = new Point2(0, 20) } },
            Arcs = new List<SketchArc> { new SketchArc { Id = "arc", Center = new Point2(30, 0), Start = new Point2(35, 0), End = new Point2(30, 5) } },
            Circles = new List<SketchCircle> { new SketchCircle { Id = "circle", Center = new Point2(50, 0), RadiusMm = 4 } },
            Polygons = new List<SketchPolygon> { new SketchPolygon { Id = "hex", Center = new Point2(70, 0), Vertex = new Point2(75, 0), Sides = 6 } },
            Slots = new List<SketchSlot> { new SketchSlot { Id = "slot", Start = new Point2(0, 30), End = new Point2(20, 30), WidthMm = 4 } },
            Splines = new List<SketchSpline> { new SketchSpline { Id = "wave", Points = new List<Point2> { new Point2(30, 30), new Point2(40, 35), new Point2(50, 30) } } },
            Points = new List<SketchPointItem> { new SketchPointItem { Id = "mark", At = new Point2(60, 30) } },
        });
        Parallel(new Vector3(0, 1, 0), kinds.Sketch.Normal, "the top plane's normal");
        Assert.Equal(6, kinds.Ids.Single(i => i.Id == "hex").References.Count);
        Assert.Equal(4, kinds.Ids.Single(i => i.Id == "slot").References.Count);
        var read = await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "kinds" });

        var block = await Block("sketch-face");
        var top = (await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = block.Path, Kind = TopologyKind.Face, Direction = new Vector3(0, 0, 1) })).Entities.Single();
        var onFace = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = block.Path,
            On = top.Reference!,
            Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = new Point2(0, 0), RadiusMm = 3 } },
        });
        Parallel(new Vector3(0, 0, 1), onFace.Sketch.Normal, "the face sketch's normal");
        Near(10, onFace.Sketch.Origin.Z, "the face sketch's origin Z");
        return $"kinds on top: normal {kinds.Sketch.Normal}, {kinds.Sketch.Solve}; read back " +
               string.Join(", ", read.Segments.GroupBy(s => (s.Kind, s.Construction)).Select(g => $"{g.Count()} {WireNames.ForEnum(g.Key.Kind)}{(g.Key.Construction ? " (construction)" : "")}")) +
               $", {read.Points.Count} points; on the block's top face: normal {onFace.Sketch.Normal}, origin {onFace.Sketch.Origin}";
    });

    [SkippableFact, Step(10)]
    public Task Dimensions_are_listed_and_set() => _run.CaseAsync(10, "model.dimensions and dimension.set on the block's depth and the plate's width", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("dimensions");
        var all = await Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = block.Path });
        var depth = all.Dimensions.Single(d => d.Feature != null && d.Feature.StartsWith("Boss-Extrude", StringComparison.Ordinal) && Math.Abs(d.Value - 10) < 1e-9);
        Assert.Equal("mm", depth.Unit);
        Assert.True(depth.Driving);

        var set = await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = block.Path, Dimension = depth.Name, Value = 15 });
        Near(15, set.Dimension.Value, "depth read back");
        Near(8000, set.Change.VolumeBeforeMm3, "volume before");
        Near(12000, set.Change.VolumeAfterMm3, "volume after");
        Near(15, set.Change.Box!.Max.Z, "box after");
        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = block.Path });
        Near(12000, mass.VolumeMm3, "volume measured");

        string plate = LiveScratchPath("sketches");
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = plate, Dimension = "width@plate", Value = 50 });
        var read = await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = plate, Sketch = "plate" });
        Near(50, Dimension(read, "width"), "width read back");
        Assert.Contains(read.Segments, s => s.Kind == SketchSegmentKind.Line && Math.Abs(s.LengthMm - 50) < 1e-6);
        return $"{all.Dimensions.Count} dimensions on the block ({string.Join(", ", all.Dimensions.Select(d => $"{d.Name}={d.Value:0.###} {d.Unit}"))}); " +
               $"{depth.Name} 10 -> 15 mm: volume {set.Change.VolumeBeforeMm3:0.###} -> {set.Change.VolumeAfterMm3:0.###} mm³; width@plate 40 -> 50 mm, its line now 50 mm";
    });

    [SkippableFact, Step(11)]
    public Task A_change_that_fails_leaves_the_part_as_it_was() => _run.CaseAsync(11, "rollback: an over-defining sketch.create and sketch.modify leave the part as it was", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = LiveScratchPath("sketches");
        int sketches = await SketchCount(path);
        var overDefined = await Refused(() => Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "right",
            Lines = new List<SketchLine> { new SketchLine { Id = "l", Start = new Point2(0, 0), End = new Point2(30, 0) } },
            Dimensions = new List<SketchDimensionItem>
            {
                new SketchDimensionItem { Entities = Of("l"), Value = 30 },
                new SketchDimensionItem { Entities = Of("l"), Value = 31 },
            },
        }));
        Assert.Contains(overDefined.Code, new[] { ErrorCode.VerificationFailed, ErrorCode.OperationFailed });
        Assert.False(overDefined.Error.Details!.ContainsKey("rollback_failed"), overDefined.Message);
        Assert.Equal(sketches, await SketchCount(path));

        var before = await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "plate" });
        var modify = await Refused(() => Call<SketchModifyRequest, SketchCreateResult>(new SketchModifyRequest
        {
            Document = path,
            Sketch = "plate",
            Add = new SketchGeometry
            {
                Lines = new List<SketchLine> { new SketchLine { Id = "extra", Start = new Point2(5, 5), End = new Point2(15, 5) } },
                Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Name = "clash", Entities = Of(before.Segments.First(s => s.Kind == SketchSegmentKind.Line).Reference!), Value = 45 } },
            },
        }));
        Assert.False(modify.Error.Details!.ContainsKey("rollback_failed"), modify.Message);
        var after = await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "plate" });
        Assert.Equal(before.Segments.Count, after.Segments.Count);
        Assert.Equal(before.Relations.Count, after.Relations.Count);
        Assert.Equal(before.Dimensions.Select(d => d.Name), after.Dimensions.Select(d => d.Name));
        Assert.Equal(SolveState.FullyDefined, after.Sketch.Solve);
        return $"sketch.create with two lengths on one line: {WireNames.ForEnum(overDefined.Code)} ({overDefined.Message}); sketches still {sketches}; " +
               $"sketch.modify adding a clashing width: {WireNames.ForEnum(modify.Code)}; plate still {after.Segments.Count} segments, {after.Relations.Count} relations, {after.Dimensions.Count} dimensions, fully defined";
    });

    /// <summary>
    /// Asks for something only a person can do in SOLIDWORKS, and waits until diag.editing (a plain read) sees it. Only
    /// then does it make one harmless model change (width@plate set to what it is), which must answer busy with
    /// user_editing; so if the user's sketch or command were not detected, nothing is changed under it. Null when it was
    /// not seen within 3 min.
    /// </summary>
    async Task<(SwAgentException Busy, DiagEditingResult Seen)?> BusyAfter(string action, Func<DiagEditingResult, bool> open)
    {
        string path = LiveScratchPath("sketches");
        double width = Dimension(await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "plate" }), "width");
        _run.Say($"ACTION: {action} (waiting up to 3 min); leave it open until the run asks you to close it");
        var clock = Stopwatch.StartNew();
        DiagEditingResult seen;
        while (!open(seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = path })))
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) return null;
            await Task.Delay(2000);
        }
        var busy = await Refused(() => Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = path, Dimension = "width@plate", Value = width }));
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("user_editing", busy.Error.Details!["reason"]);
        return (busy, seen);
    }

    /// <summary>Asks for what <see cref="BusyAfter"/> opened to be closed, waits until diag.editing sees nothing open, then makes the harmless change, which must work.</summary>
    async Task<TimeSpan> FreeAfter(string action)
    {
        string path = LiveScratchPath("sketches");
        _run.Say($"ACTION: {action} (waiting up to 3 min)");
        var clock = Stopwatch.StartNew();
        DiagEditingResult seen;
        while ((seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = path })).Sketch != null || seen.Command != null)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw _run.Stop($"the user's {seen.Sketch ?? seen.Command} was still open after 3 min");
            await Task.Delay(2000);
        }
        double width = Dimension(await Call<SketchReadRequest, SketchReadResult>(new SketchReadRequest { Document = path, Sketch = "plate" }), "width");
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = path, Dimension = "width@plate", Value = width });
        return clock.Elapsed;
    }

    [SkippableFact, Step(12)]
    public Task A_sketch_the_user_is_editing_makes_changes_wait() => _run.CaseAsync(12, "busy with user_editing while the user edits a sketch (by hand)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        int sketches = await SketchCount(LiveScratchPath("sketches"));
        var (busy, seen) = await BusyAfter("in SOLIDWORKS, in sketches.SLDPRT, right-click the sketch 'kinds' in the feature tree and choose Edit Sketch", s => s.Sketch != null)
            ?? throw new SkipException("no sketch was seen open for editing within 3 min");
        Assert.Equal("before", busy.Error.Details!["detected"]);
        string sketch = (string)busy.Error.Details["sketch"]!;
        Assert.Equal(seen.Sketch, sketch);
        var waited = await FreeAfter("exit the sketch now (the exit-sketch icon in the corner of the graphics area)");
        Assert.Equal(sketches, await SketchCount(LiveScratchPath("sketches")));
        return $"busy, user_editing, sketch {sketch}, detected before ({busy.Message}); worked again {waited.TotalSeconds:0} s after asking for the sketch to be exited";
    });

    [SkippableFact, Step(13)]
    public Task A_property_manager_the_user_has_open_makes_changes_wait() => _run.CaseAsync(13, "busy with user_editing while the user has a PropertyManager open (by hand)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (busy, seen) = await BusyAfter("in SOLIDWORKS, in sketches.SLDPRT, click Features > Extruded Boss/Base so its PropertyManager opens", s => s.Command != null)
            ?? throw new SkipException("no PropertyManager was seen open within 3 min: either nobody opened one, or GetRunningCommandInfo does not detect it (then the design's fallback is needed: report it)");
        string command = (string)busy.Error.Details!["command"]!;
        Assert.Equal(seen.Command, command);
        var waited = await FreeAfter("close the PropertyManager with its red X (Cancel)");
        return $"busy, user_editing, command '{command}' (GetRunningCommandInfo), detected before; worked again {waited.TotalSeconds:0} s after asking for it to be closed";
    });

    Task<ModelFindResult> Find(string document, ModelFindRequest r)
    {
        r.Document = document;
        return Call<ModelFindRequest, ModelFindResult>(r);
    }

    /// <summary>The one face, edge or vertex model.find has within 0.001 mm of <paramref name="at"/>.</summary>
    async Task<EntityInfo> At(string document, TopologyKind kind, Point3 at, GeometryType? geometry = null) =>
        Assert.Single((await Find(document, new ModelFindRequest { Kind = kind, Near = at, MaxDistanceMm = 0.001, Geometry = geometry })).Entities);

    async Task<double> VolumeOf(string document) => (await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = document })).VolumeMm3;

    [SkippableFact, Step(14)]
    public Task Extrudes_make_a_block_and_cut_a_hole_exactly() => _run.CaseAsync(14, "feature.extrude: a 40 x 20 x 10 boss from a sketch, then a 5 mm radius hole cut through all", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("features");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var outline = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = new Point2(0, 0), Corner2 = new Point2(40, 20) } },
        });
        var boss = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = outline.Sketch.Name, DepthMm = 10 });
        Near(8000, boss.Change.VolumeAfterMm3, "the boss's volume");
        Assert.Equal(6, boss.Faces.Count);
        Assert.Contains(boss.Checks, c => c.Contains("mm² × 10 mm"));
        var hole = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = new Point2(20, 10), RadiusMm = 5 } },
        });
        var cut = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = hole.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        Near(8000 - Math.PI * 25 * 10, await VolumeOf(path), "the volume after the hole");
        var bore = Assert.Single(cut.Faces, f => f.Geometry == GeometryType.Cylinder);
        Near(5, bore.RadiusMm!.Value, "the hole's radius");
        return $"{boss.Feature.Name}: {string.Join("; ", boss.Checks)}; {cut.Feature.Name}: {string.Join("; ", cut.Checks)}, its faces {string.Join(", ", cut.Faces.Select(f => WireNames.ForEnum(f.Geometry)))}";
    });

    [SkippableFact, Step(15)]
    public Task A_revolve_makes_a_tube_of_Pappus_volume() => _run.CaseAsync(15, "feature.revolve: a 10 x 30 profile turned about a centerline, a tube of Pappus' volume", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = NewPart("revolve");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var profile = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "wall", Corner1 = new Point2(10, 0), Corner2 = new Point2(20, 30) } },
            CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = new Point2(0, 0), End = new Point2(0, 40) } },
        });
        string axis = profile.Ids.Single(i => i.Id == "axis").References[0];
        var tube = await Call<FeatureRevolveRequest, FeatureResult>(new FeatureRevolveRequest { Document = path, Sketch = profile.Sketch.Name, Axis = axis });
        Near(Math.PI * (400 - 100) * 30, await VolumeOf(path), "the tube's volume");
        Assert.Contains(tube.Checks, c => c.Contains("Pappus"));
        Assert.Equal(2, tube.Faces.Count(f => f.Geometry == GeometryType.Cylinder));
        return $"{tube.Feature.Name}: {string.Join("; ", tube.Checks)}; faces {string.Join(", ", tube.Faces.Select(f => WireNames.ForEnum(f.Geometry)))}";
    });

    [SkippableFact, Step(16)]
    public Task A_fillet_and_a_chamfer_take_exact_volume_off_the_corners() => _run.CaseAsync(16, "feature.fillet and feature.chamfer on two of the block's 10 mm edges, by exact volume", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("edges");
        string path = block.Path!;
        var corner = await At(path, TopologyKind.Edge, new Point3(40, 20, 5));
        var fillet = await Call<FeatureFilletRequest, FeatureResult>(new FeatureFilletRequest { Document = path, Edges = new List<string> { corner.Reference! }, RadiusMm = 2 });
        Near(8000 - (4 - Math.PI) * 10, await VolumeOf(path), "the volume after a 2 mm fillet on a 10 mm edge");
        Assert.Contains(fillet.Faces, f => f.Geometry == GeometryType.Cylinder);
        var other = await At(path, TopologyKind.Edge, new Point3(0, 0, 5));
        var chamfer = await Call<FeatureChamferRequest, FeatureResult>(new FeatureChamferRequest { Document = path, Edges = new List<string> { other.Reference! }, DistanceMm = 2 });
        Near(8000 - (4 - Math.PI) * 10 - 20, await VolumeOf(path), "the volume after a 2 mm 45° chamfer on a 10 mm edge");
        return $"{fillet.Feature.Name}: {string.Join("; ", fillet.Checks)}; {chamfer.Feature.Name}: {string.Join("; ", chamfer.Checks)}";
    });

    [SkippableFact, Step(17)]
    public Task Reference_geometry_is_where_it_was_asked_to_be() => _run.CaseAsync(17, "refgeom.plane, refgeom.axis and refgeom.point on the block with the hole", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = LiveScratchPath("features");
        var notes = new List<string>();
        // direction also matches a cylinder whose axis is parallel (model.find's contract), as the hole's bore is (3.C live run).
        var top = Assert.Single((await Find(path, new ModelFindRequest { Kind = TopologyKind.Face, Geometry = GeometryType.Plane, Direction = new Vector3(0, 0, 1) })).Entities);
        var offset = await Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Offset, From = top.Reference!, DistanceMm = 15 });
        Near(25, offset.Feature.Origin!.Z, "the offset plane's Z");
        notes.Add($"{offset.Feature.Name} offset: {string.Join("; ", offset.Checks)}");
        var bottomEdge = await At(path, TopologyKind.Edge, new Point3(20, 0, 0));
        var angled = await Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.Angle, From = "front", Axis = bottomEdge.Reference!, AngleDeg = 30 });
        notes.Add($"{angled.Feature.Name} angle: {string.Join("; ", angled.Checks)}");
        var points = new List<string>();
        foreach (var p in new[] { new Point3(0, 0, 0), new Point3(40, 0, 0), new Point3(0, 20, 10) }) points.Add((await At(path, TopologyKind.Vertex, p)).Reference!);
        var three = await Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = path, Mode = PlaneMode.ThreePoints, Points = points });
        notes.Add($"{three.Feature.Name} three points: {string.Join("; ", three.Checks)}");
        var meet = await Call<RefgeomAxisRequest, FeatureResult>(new RefgeomAxisRequest { Document = path, Mode = AxisMode.TwoPlanes, Entities = new List<string> { "front", "right" } });
        Parallel(new Vector3(0, 1, 0), meet.Feature.Axis!, "front and right meet along Y");
        var bore = Assert.Single((await Find(path, new ModelFindRequest { Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder })).Entities);
        var bored = await Call<RefgeomAxisRequest, FeatureResult>(new RefgeomAxisRequest { Document = path, Mode = AxisMode.Cylinder, Entities = new List<string> { bore.Reference! } });
        Parallel(new Vector3(0, 0, 1), bored.Feature.Axis!, "the hole's axis");
        var rim = await At(path, TopologyKind.Edge, new Point3(25, 10, 10), GeometryType.Circle);
        var centre = await Call<RefgeomPointRequest, FeatureResult>(new RefgeomPointRequest { Document = path, Mode = PointMode.ArcCenter, Entities = new List<string> { rim.Reference! } });
        Near(20, centre.Feature.Origin!.X, "the rim's centre X");
        Near(10, centre.Feature.Origin.Y, "the rim's centre Y");
        Near(10, centre.Feature.Origin.Z, "the rim's centre Z");
        notes.Add($"{meet.Feature.Name} along {meet.Feature.Axis}; {bored.Feature.Name} along {bored.Feature.Axis}; {centre.Feature.Name} at {centre.Feature.Origin}");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(18)]
    public Task Suppress_and_delete_change_the_part_and_refuse_children() => _run.CaseAsync(18, "feature.suppress and feature.delete on the hole: volume back and forth, children refused until asked", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = LiveScratchPath("features");
        string cut = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Single(f => f.Type == "ICE" || f.Type == "Cut").Name;
        await Call<FeatureSuppressRequest, FeatureSuppressResult>(new FeatureSuppressRequest { Document = path, Features = new List<string> { cut } });
        Near(8000, await VolumeOf(path), "the volume with the hole suppressed");
        await Call<FeatureSuppressRequest, FeatureSuppressResult>(new FeatureSuppressRequest { Document = path, Features = new List<string> { cut }, Suppressed = false });
        Near(8000 - Math.PI * 250, await VolumeOf(path), "the volume with the hole back");
        var refused = await Refused(() => Call<FeatureDeleteRequest, FeatureDeleteResult>(new FeatureDeleteRequest { Document = path, Features = new List<string> { cut } }));
        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        Assert.Equal("children", refused.Error.Details!["field"]);
        var deleted = await Call<FeatureDeleteRequest, FeatureDeleteResult>(new FeatureDeleteRequest { Document = path, Features = new List<string> { cut }, Children = DeleteChildren.Delete });
        Near(8000, await VolumeOf(path), "the volume with the hole deleted");
        Assert.DoesNotContain((await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features, f => f.Type == "ICE" || f.Type == "Cut");
        return $"{cut} suppressed: 8000 mm³, unsuppressed: {8000 - Math.PI * 250:0.###} mm³; delete refused ({refused.Message}); deleted with children: {string.Join(", ", deleted.Deleted)}";
    });

    [SkippableFact, Step(19)]
    public Task Measure_gives_exact_distances_and_angles() => _run.CaseAsync(19, "model.measure between the block's faces, vertices and planes", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("measuring");
        string path = block.Path!;
        var top = Assert.Single((await Find(path, new ModelFindRequest { Kind = TopologyKind.Face, Geometry = GeometryType.Plane, Direction = new Vector3(0, 0, 1) })).Entities);
        var bottom = Assert.Single((await Find(path, new ModelFindRequest { Kind = TopologyKind.Face, Direction = new Vector3(0, 0, -1) })).Entities);
        var faces = await Call<ModelMeasureRequest, ModelMeasureResult>(new ModelMeasureRequest { Document = path, A = top.Reference!, B = bottom.Reference! });
        Near(10, faces.DistanceMm, "top to bottom");
        Near(0, faces.AngleDeg!.Value, "the angle between top and bottom", 1e-6);
        var a = await At(path, TopologyKind.Vertex, new Point3(0, 0, 0));
        var b = await At(path, TopologyKind.Vertex, new Point3(40, 20, 10));
        var corners = await Call<ModelMeasureRequest, ModelMeasureResult>(new ModelMeasureRequest { Document = path, A = a.Reference!, B = b.Reference! });
        Near(Math.Sqrt(1600 + 400 + 100), corners.DistanceMm, "corner to corner");
        Near(Math.Sqrt(1600 + 400 + 100), corners.CenterDistanceMm!.Value, "corner to corner, centres");
        var planes = await Call<ModelMeasureRequest, ModelMeasureResult>(new ModelMeasureRequest { Document = path, A = "front", B = "right" });
        Near(90, planes.AngleDeg!.Value, "front to right", 1e-6);
        return $"top to bottom {faces.DistanceMm} mm at {faces.AngleDeg}°; corners {corners.DistanceMm:0.######} mm (points {corners.PointA} and {corners.PointB}); front to right {planes.AngleDeg}°, {planes.DistanceMm} mm";
    });

    [SkippableFact, Step(20)]
    public Task Features_SOLIDWORKS_cannot_make_leave_the_part_as_it_was() => _run.CaseAsync(20, "rollback: a fillet too big for its edge and a cut that misses the part leave it as it was", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Block("rollback");
        string path = block.Path!;
        int before = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features.Count;
        var edge = await At(path, TopologyKind.Edge, new Point3(40, 20, 5));
        // 3.C round 3: SOLIDWORKS makes a 50 mm fillet on this 10 mm edge (a cylinder face that overflows both 40 and 20 mm
        // faces: volume 8000 -> 5591.19 mm³, no rebuild errors), so it is not a fillet SOLIDWORKS cannot make. Either way the
        // part must end as it was: a refusal leaves it so, and a fillet made is taken out again with feature.delete.
        FeatureResult? madeBig = null;
        var bigError = await Record.ExceptionAsync(async () =>
            madeBig = await Call<FeatureFilletRequest, FeatureResult>(new FeatureFilletRequest { Document = path, Edges = new List<string> { edge.Reference! }, RadiusMm = 50 }));
        string fillet;
        if (madeBig != null)
        {
            var removed = await Call<FeatureDeleteRequest, FeatureDeleteResult>(new FeatureDeleteRequest { Document = path, Features = new List<string> { madeBig.Feature.Name! } });
            Assert.Contains(madeBig.Feature.Name!, removed.Deleted);
            fillet = $"made by SOLIDWORKS ({madeBig.Feature.Name}: faces {string.Join(", ", madeBig.Faces.Select(f => f.Geometry))}; " +
                $"volume {madeBig.Change.VolumeBeforeMm3:0.###} -> {madeBig.Change.VolumeAfterMm3:0.###} mm³; rebuild errors {madeBig.Change.Rebuild.Errors}), then deleted";
        }
        else
        {
            var big = Assert.IsType<SwAgentException>(bigError);
            Assert.Contains(big.Code, new[] { ErrorCode.OperationFailed, ErrorCode.VerificationFailed });
            Assert.False(big.Error.Details!.ContainsKey("rollback_failed"));
            fillet = $"{WireNames.ForEnum(big.Code)} ({big.Message})";
        }
        var away = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "away", Center = new Point2(100, 100), RadiusMm = 5 } },
        });
        var miss = await Refused(() => Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = away.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll }));
        Assert.Contains(miss.Code, new[] { ErrorCode.OperationFailed, ErrorCode.VerificationFailed });
        var tree = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = path })).Features;
        Assert.Equal(before + 1, tree.Count);
        Assert.DoesNotContain(tree, f => f.Type == "Fillet" || f.Type == "ICE" || f.Type == "Cut");
        Near(8000, await VolumeOf(path), "the volume after both");
        var status = await Call<ModelRebuildStatusRequest, RebuildResult>(new ModelRebuildStatusRequest { Document = path });
        Assert.Equal(0, status.Errors);
        return $"fillet r 50: {fillet}; cut away from the part: {WireNames.ForEnum(miss.Code)} ({miss.Message}); the sketch kept, volume 8000 mm³, no rebuild errors";
    });

    /// <summary>A new part with a 40 x 20 rectangle at the origin on the front plane; its path and the sketch's name.</summary>
    async Task<(string Path, string Sketch)> Outlined(string name)
    {
        string path = NewPart(name);
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path,
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = new Point2(0, 0), Corner2 = new Point2(40, 20) } },
        });
        return (path, sketch.Sketch.Name);
    }

    async Task<Box3> BoxOf(string document) =>
        (await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = document, Precise = true })).Box!;

    /// <summary>
    /// The ends and options only SOLIDWORKS can judge: whether swEndCondUpToNext is the end condition for up_to_next,
    /// which way mid_plane, reverse and a second direction go (by the bounding box), that a draft narrows the profile
    /// (by the drafted volume), and that a thin wall is the wall asked for.
    /// </summary>
    [SkippableFact, Step(21)]
    public Task Extrude_ends_and_options_make_the_geometry_asked_for() => _run.CaseAsync(21, "feature.extrude: up_to_next, mid_plane, reverse, a second direction, draft and a thin wall, by bounding box and exact volume", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var notes = new List<string>();

        var (mid, midSketch) = await Outlined("mid-plane");
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = mid, Sketch = midSketch, End = ExtrudeEnd.MidPlane, DepthMm = 10 });
        var box = await BoxOf(mid);
        Near(-5, box.Min.Z, "mid_plane min Z");
        Near(5, box.Max.Z, "mid_plane max Z");
        Near(8000, await VolumeOf(mid), "mid_plane volume");
        notes.Add("mid_plane: z -5 to 5, 8000 mm³");

        var (back, backSketch) = await Outlined("reverse");
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = back, Sketch = backSketch, DepthMm = 10, Reverse = true });
        box = await BoxOf(back);
        Near(-10, box.Min.Z, "reverse min Z");
        Near(0, box.Max.Z, "reverse max Z");
        notes.Add("reverse: z -10 to 0");

        var (both, bothSketch) = await Outlined("both-ways");
        var twice = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest
        {
            Document = both,
            Sketch = bothSketch,
            DepthMm = 10,
            Direction2 = new ExtrudeDirection { DepthMm = 5 },
        });
        box = await BoxOf(both);
        Near(-5, box.Min.Z, "second direction min Z");
        Near(10, box.Max.Z, "second direction max Z");
        Near(12000, await VolumeOf(both), "both ways volume");
        notes.Add($"direction2: z -5 to 10, {string.Join("; ", twice.Checks)}");

        var (drafted, draftSketch) = await Outlined("draft");
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = drafted, Sketch = draftSketch, DepthMm = 10, DraftDeg = 5 });
        double k = 2 * Math.Tan(5 * Math.PI / 180);
        double narrowing = 8000 - 3000 * k + 1000 * k * k / 3;
        Near(narrowing, await VolumeOf(drafted), "a 5° inward draft's volume (∫ (40 - kt)(20 - kt) dt over 0 to 10)");
        notes.Add($"draft 5° inward: {narrowing:0.###} mm³");

        string thinPath = NewPart("thin");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = thinPath });
        var ring = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = thinPath,
            On = "front",
            Circles = new List<SketchCircle> { new SketchCircle { Id = "ring", Center = new Point2(0, 0), RadiusMm = 10 } },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest
        {
            Document = thinPath,
            Sketch = ring.Sketch.Name,
            DepthMm = 10,
            Thin = new ExtrudeThin { ThicknessMm = 1, Side = ThinSide.MidPlane },
        });
        Near(Math.PI * (10.5 * 10.5 - 9.5 * 9.5) * 10, await VolumeOf(thinPath), "a 1 mm mid-plane wall round a 10 mm radius, 10 mm deep");
        notes.Add($"thin mid_plane: {Math.PI * 200:0.###} mm³");

        var (next, nextSketch) = await Outlined("up-to-next");
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = next, Sketch = nextSketch, DepthMm = 10 });
        var below = await Call<RefgeomPlaneRequest, FeatureResult>(new RefgeomPlaneRequest { Document = next, Mode = PlaneMode.Offset, From = "front", DistanceMm = 20, Flip = true });
        Near(-20, below.Feature.Origin!.Z, "the plane below the block");
        var mark = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = next,
            On = below.Feature.Name!,
            Points = new List<SketchPointItem> { new SketchPointItem { Id = "at", At = new Point2(0, 0) } },
        });
        var s = mark.Sketch;
        double dx = 20 - s.Origin.X, dy = 10 - s.Origin.Y, dz = -20 - s.Origin.Z;
        var under = new Point2(dx * s.XAxis.X + dy * s.XAxis.Y + dz * s.XAxis.Z, dx * s.YAxis.X + dy * s.YAxis.Y + dz * s.YAxis.Z);
        await Call<SketchModifyRequest, SketchCreateResult>(new SketchModifyRequest
        {
            Document = next,
            Sketch = s.Name,
            Add = new SketchGeometry { Circles = new List<SketchCircle> { new SketchCircle { Id = "post", Center = under, RadiusMm = 5 } } },
        });
        var post = await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = next, Sketch = s.Name, End = ExtrudeEnd.UpToNext, Reverse = s.Normal.Z < 0 });
        Near(8000 + Math.PI * 25 * 20, await VolumeOf(next), "the post up to the block's underside");
        box = await BoxOf(next);
        Near(-20, box.Min.Z, "the post's foot");
        notes.Add($"up_to_next: {post.Feature.Name} from z -20 to the block, the circle at {under} in a sketch with normal {s.Normal}");
        return string.Join(" | ", notes);
    });

    [SkippableFact, Step(22)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(22, "the suite's documents closed", async () =>
    {
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        int closed = 0;
        foreach (var d in state.Documents.Where(d => d.Path != null))
        {
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true });
            closed++;
        }
        // Read once: the add-in refreshes its document table before it answers (#11), so a close is already reflected.
        var open = (await _run.AddinStateAsync()).Documents;
        Assert.True(open.Count == 0, $"still open after closing {closed}: {string.Join(", ", open.Select(d => d.Path ?? d.Title))}");
        return $"closed {closed} documents; none open";
    });

    string LiveScratchPath(string name) => Path.Combine(_run.Guard.ScratchDirectory, name + ".SLDPRT");
}
