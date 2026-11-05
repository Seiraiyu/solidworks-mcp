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
/// The parts live suite (design 371, Testing; plan 3.A covers documents, references, inspection and selection):
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

    [SkippableFact, Step(8)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(8, "the suite's documents closed", async () =>
    {
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        int closed = 0;
        foreach (var d in state.Documents.Where(d => d.Path != null))
        {
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true });
            closed++;
        }
        Assert.Empty((await _run.AddinStateAsync()).Documents);
        return $"closed {closed} documents; none open";
    });

    string LiveScratchPath(string name) => Path.Combine(_run.Guard.ScratchDirectory, name + ".SLDPRT");
}
