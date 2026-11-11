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

/// <summary>The assemblies suite's run: its own id (…-assemblies-suite), directory, scratch directory and report.</summary>
public sealed class AssembliesSuiteRun : LiveRun
{
    public AssembliesSuiteRun()
        : base("assemblies-suite")
    {
    }
}

/// <summary>
/// The assemblies live suite (design 455, Testing; plan 4.A covers assembly documents, components and the in-context
/// precondition, cases 1 to 9; plan 4.B assembly references and inspection, cases 10 to 15): every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly as swmcp serve does, in
/// [Step] order, and checks positions and states against exact values. It builds its own parts (diag.scratch_block) and
/// assemblies in its scratch directory, records each before creating it, never ends SOLIDWORKS, stops the run if a
/// document it did not create is open, and closes everything it made at the end. Skipped unless SWMCP_LIVE=1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class AssembliesSuite : IClassFixture<AssembliesSuiteRun>
{
    const double Tol = 0.001;

    static readonly string[] AssemblyPreferences =
    {
        "insert_component_for_new_assembly", "auto_load_parts_lightweight", "always_resolve_subassemblies",
        "large_asm_mode_enabled", "large_asm_mode_auto_load_lightweight", "large_asm_mode_use_large_design_review",
    };

    readonly LiveRun _run;

    public AssembliesSuite(AssembliesSuiteRun run)
    {
        _run = run;
    }

    Supervisor Sw => _run.Supervisor;

    Task<TResult> Call<TRequest, TResult>(TRequest request)
        where TRequest : class
        where TResult : class =>
        Sw.CallAsync<TRequest, TResult>(request, TimeSpan.FromSeconds(120));

    static async Task<SwAgentException> Refused(Func<Task> call) => await Assert.ThrowsAsync<SwAgentException>(call);

    static void Near(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= Tol, $"{what}: expected {expected}, got {actual}");

    /// <summary>A scratch path for a new document, recorded as this run's before anything creates it.</summary>
    string New(string name, string extension)
    {
        string path = _run.Guard.NewScratchPath(name, extension);
        _run.Guard.Created(path);
        return path;
    }

    /// <summary>A block part saved in the scratch directory and closed again, so inserting it loads it as SOLIDWORKS does (hidden).</summary>
    async Task<string> ClosedBlock(string name, double x, double y, double z)
    {
        var block = await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = New(name, ".SLDPRT"), XMm = x, YMm = y, ZMm = z });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = block.Path });
        return block.Path!;
    }

    async Task<string> NewAssembly(string name)
    {
        var made = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Assembly, Path = New(name, ".SLDASM") });
        return made.Document.Path!;
    }

    Task<AssemblyComponentsResult> Components(string assembly, bool recursive = false) =>
        Call<AssemblyComponentsRequest, AssemblyComponentsResult>(new AssemblyComponentsRequest { Document = assembly, Recursive = recursive });

    async Task<ComponentInfo> Component(string assembly, string name) =>
        (await Components(assembly, recursive: true)).Components.Single(c => c.Name == name);

    [SkippableFact, Step(1)]
    public Task SOLIDWORKS_is_ready_with_nothing_of_the_owners_open() => _run.CaseAsync(1, "SOLIDWORKS ready, only the suite's documents open, diagnostics on, assembly preferences applied", async () =>
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
        Assert.True(state.PreferencesApplied, "the automation preferences are not applied");
        var notTaken = state.PreferencesNotApplied.Intersect(AssemblyPreferences).ToList();
        Assert.True(notTaken.Count == 0, "assembly preferences SOLIDWORKS did not take: " + string.Join(", ", notTaken));
        return $"SOLIDWORKS {status.SwRevision}, add-in {status.AddinVersion}, protocol {status.ProtocolVersion}; the six assembly preferences took";
    });

    [SkippableFact, Step(2)]
    public Task A_new_assembly_leaves_no_command_open() => _run.CaseAsync(2, "document.new type assembly: planes, origin, no Begin Assembly", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = New("new", ".SLDASM");
        var made = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Assembly, Path = path });
        Assert.Equal(DocumentType.Assembly, made.Document.Type);
        Assert.True(File.Exists(path));
        Assert.True(made.Document.Visible);
        Assert.Empty((await Components(path)).Components);
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path });
        return $"made {Path.GetFileName(path)} from {Path.GetFileName(made.Template)}; verification found no open command";
    });

    [SkippableFact, Step(3)]
    public Task Components_go_where_they_are_put_and_report_their_state() => _run.CaseAsync(3, "assembly.insert at a point and turned; assembly.components; component.place", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string basePart = await ClosedBlock("base", 40, 20, 10);
        string post = await ClosedBlock("post", 10, 10, 30);
        string asm = await NewAssembly("placing");

        var first = await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = basePart });
        Assert.True(first.Component.Fixed);
        Assert.Equal(Constraint.Fixed, first.Component.Constraint);
        Near(40, first.Component.Box!.Max.X, "the base's box, x");

        // Turned a quarter about z: the post's x axis along the assembly's y. Its own x (0..10) then runs along the
        // assembly's y, and its own y (0..10) along the assembly's -x, so the box proves which way the array is read.
        var second = await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest
        {
            Document = asm, Path = post, At = new Point3(100, 0, 0), XAxis = new Vector3(0, 1, 0), ZAxis = new Vector3(0, 0, 1),
        });
        Assert.False(second.Component.Fixed);
        Assert.Equal(Constraint.UnderDefined, second.Component.Constraint);
        var box = second.Component.Box!;
        Near(90, box.Min.X, "turned post box min x");
        Near(100, box.Max.X, "turned post box max x");
        Near(0, box.Min.Y, "turned post box min y");
        Near(10, box.Max.Y, "turned post box max y");
        Near(30, box.Max.Z, "turned post box max z");

        var listed = await Components(asm);
        Assert.Equal(new[] { first.Component.Name, second.Component.Name }, listed.Components.Select(c => c.Name));
        Assert.All(listed.Components, c => Assert.StartsWith("r1.component.", c.Reference));
        Assert.All(listed.Components, c => Assert.Equal(ComponentState.Resolved, c.State));
        var hidden = (await _run.AddinStateAsync()).Documents.Where(d => !d.Visible).Select(d => Path.GetFileName(d.Path)).ToList();
        Assert.Contains("post.SLDPRT", hidden);

        var placed = await Call<ComponentPlaceRequest, ComponentPlaceResult>(new ComponentPlaceRequest { Document = asm, Component = second.Component.Reference!, Origin = new Point3(150, 0, 0) });
        Near(150, placed.Component.Transform.Origin.X, "placed origin x");
        Near(1, placed.Component.Transform.XAxis.Y, "placed keeps its axes");
        Assert.Equal(second.Component.Name, placed.Change.Moved.Single().Name);
        var fixedRefused = await Refused(() => Call<ComponentPlaceRequest, ComponentPlaceResult>(new ComponentPlaceRequest { Document = asm, Component = first.Component.Name, Origin = new Point3(5, 0, 0) }));
        Assert.Equal(ErrorCode.InvalidArgument, fixedRefused.Code);

        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{first.Component.Name} fixed at the origin; {second.Component.Name} turned at (100, 0, 0), box {box.Min}..{box.Max}; placed at {placed.Component.Transform.Origin}; " +
               $"placing the fixed base refused; hidden documents: {string.Join(", ", hidden)}";
    });

    [SkippableFact, Step(4)]
    public Task Component_state_is_set_read_back_and_survives_a_reopen() => _run.CaseAsync(4, "component.set fixed, hidden, suppressed, resolved; references across close and reopen", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("placing", ".SLDASM");
        var post = await Component(asm, "post-1");
        var set = await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { post.Reference! }, Fixed = true, Hidden = true });
        Assert.True(set.Components[0].Fixed);
        Assert.True(set.Components[0].Hidden);
        Assert.Equal(Constraint.Fixed, set.Components[0].Constraint);
        var suppressed = await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "post-1" }, Suppression = Suppression.Suppressed });
        Assert.Equal(ComponentState.Suppressed, suppressed.Components[0].State);
        Assert.Null(suppressed.Components[0].Box);
        var resolved = await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { post.Reference! }, Suppression = Suppression.Resolved, Fixed = false, Hidden = false });
        Assert.Equal(ComponentState.Resolved, resolved.Components[0].State);
        Near(150, resolved.Components[0].Transform.Origin.X, "back where it was");

        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        var closed = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm });
        // By file name: SOLIDWORKS titles carry the extension only when Windows shows extensions (4.A live run, round 3).
        Assert.Equal(new[] { "base.SLDPRT", "post.SLDPRT" }, closed.Unloaded.Select(u => Path.GetFileName(u.Path!)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = asm });
        var again = await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { post.Reference! }, Hidden = true });
        Assert.Equal("post-1", again.Components[0].Name);
        await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { post.Reference! }, Hidden = false });
        return $"fixed and hidden, suppressed (no box), resolved at {resolved.Components[0].Transform.Origin}; closing unloaded {string.Join(", ", closed.Unloaded.Select(u => u.Title))}; " +
               "the reference issued before the close named post-1 after the reopen";
    });

    [SkippableFact, Step(5)]
    public Task A_subassembly_goes_flexible_and_its_components_are_named_by_path() => _run.CaseAsync(5, "a subassembly inserted rigid, made flexible, its component placed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string pin = await ClosedBlock("pin", 10, 10, 10);
        string sub = await NewAssembly("sub");
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = sub, Path = pin });
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = sub, Path = pin, At = new Point3(0, 0, 50) });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = sub });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = sub });

        string asm = LiveScratchPath("placing", ".SLDASM");
        var inserted = await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = sub, At = new Point3(0, 100, 0) });
        Assert.True(inserted.Component.Subassembly);
        Assert.Equal(SolveAs.Rigid, inserted.Component.SolveAs);
        var tree = await Components(asm, recursive: true);
        string nested = tree.Components.Single(c => c.Parent == inserted.Component.Name && Math.Abs(c.Transform.Origin.Z - 50) < Tol).Name;
        Assert.StartsWith(inserted.Component.Name + "/", nested);
        var rigid = await Refused(() => Call<ComponentPlaceRequest, ComponentPlaceResult>(new ComponentPlaceRequest { Document = asm, Component = nested, Origin = new Point3(0, 100, 80) }));
        Assert.Equal(inserted.Component.Name, rigid.Error.Details!["subassembly"]);

        var flexible = await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { inserted.Component.Name }, SolveAs = SolveAs.Flexible });
        Assert.Equal(SolveAs.Flexible, flexible.Components[0].SolveAs);
        var moved = await Call<ComponentPlaceRequest, ComponentPlaceResult>(new ComponentPlaceRequest { Document = asm, Component = nested, Origin = new Point3(0, 100, 80) });
        Near(80, moved.Component.Transform.Origin.Z, "the nested pin's z");
        await Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { inserted.Component.Name } });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{inserted.Component.Name} inserted rigid; placing {nested} refused while rigid; flexible; {nested} placed at {moved.Component.Transform.Origin}; the subassembly deleted again";
    });

    [SkippableFact, Step(6)]
    public Task Saving_writes_dirty_components_and_closing_protects_them() => _run.CaseAsync(6, "document.save writes a dirty component; still_used_by; close refuses a dirty hidden component", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("placing", ".SLDASM");
        string post = LiveScratchPath("post", ".SLDPRT");
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = post });
        var dims = await Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = post });
        var height = dims.Dimensions.Single(d => d.Feature != null && d.Feature.StartsWith("Boss-Extrude", StringComparison.Ordinal) && Math.Abs(d.Value - 30) < 1e-9);
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = post, Dimension = height.Name, Value = 35 });
        var before = File.GetLastWriteTimeUtc(post);

        var saved = await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        Assert.Contains(saved.Referenced, r => string.Equals(r.Path, post, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.GetLastWriteTimeUtc(post) > before, "the post's file was not written");

        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = post, Dimension = height.Name, Value = 40 });
        var window = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = post });
        Assert.Equal(new[] { asm }, window.StillUsedBy, StringComparer.OrdinalIgnoreCase);
        Assert.False(window.Discarded);
        var hiddenPost = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, post, StringComparison.OrdinalIgnoreCase));
        Assert.False(hiddenPost.Visible);
        Assert.True(hiddenPost.Dirty);

        var refused = await Refused(() => Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm }));
        Assert.Equal("discard", refused.Error.Details!["field"]);
        Assert.Contains(post, ((System.Collections.Generic.List<object?>)refused.Error.Details["unsaved"]!).Cast<string>(), StringComparer.OrdinalIgnoreCase);
        var closed = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm, Discard = true });
        Assert.True(closed.Discarded);
        Assert.DoesNotContain((await _run.AddinStateAsync()).Documents, d => string.Equals(d.Path, post, StringComparison.OrdinalIgnoreCase));
        return $"save wrote {string.Join(", ", saved.Referenced.Select(r => Path.GetFileName(r.Path)))} then the assembly; the post's window closed, still used by the assembly, dirty and hidden; " +
               $"closing the assembly refused ({refused.Message}); closed with discard, unloading {string.Join(", ", closed.Unloaded.Select(u => u.Title))}";
    });

    [SkippableFact, Step(7)]
    public Task A_missing_component_is_listed_without_a_dialog() => _run.CaseAsync(7, "document.open lists a missing component file; assembly.components says missing", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        // A name no earlier run used: SOLIDWORKS looks for a missing file in folders it has opened this session, and loaded
        // another run's gone.SLDPRT instead (4.A live run, round 2).
        string gone = await ClosedBlock("gone-" + Guid.NewGuid().ToString("N").Substring(0, 8), 10, 10, 10);
        string asm = await NewAssembly("missing");
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = gone });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm });
        File.Move(gone, gone + ".moved");
        try
        {
            var opened = await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = asm });
            Assert.Contains(gone, opened.Missing, StringComparer.OrdinalIgnoreCase);
            var component = (await Components(asm)).Components.Single();
            Assert.Equal(ComponentState.Missing, component.State);
            Assert.Null((await _run.AddinStateAsync()).Dialog);
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm, Discard = true });
            return $"missing: {string.Join(", ", opened.Missing)}; {component.Name} is missing; no dialog; warnings: {string.Join(", ", opened.Warnings.DefaultIfEmpty("none"))}";
        }
        finally
        {
            File.Move(gone + ".moved", gone);
        }
    });

    [SkippableFact, Step(8)]
    public Task Failures_leave_the_assembly_as_it_was() => _run.CaseAsync(8, "a configuration the file lacks rolled back; a deleted component's reference unresolved", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("placing", ".SLDASM");
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = asm });
        int count = (await Components(asm)).Components.Count;
        var config = await Refused(() => Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = LiveScratchPath("pin", ".SLDPRT"), Configuration = "NoSuchConfiguration" }));
        Assert.Equal(ErrorCode.VerificationFailed, config.Code);
        Assert.Equal(count, (await Components(asm)).Components.Count);

        var post = await Component(asm, "post-1");
        var deleted = await Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { post.Reference! } });
        Assert.Equal(new[] { "post-1" }, deleted.Deleted);
        var stale = await Refused(() => Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { post.Reference! }, Hidden = true }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
        return $"configuration refused ({config.Message}), still {count} components; post-1 deleted; its reference: {WireNames.ForEnum(stale.Code)} ({stale.Error.Details!["reason"]})";
    });

    [SkippableFact, Step(9)]
    public Task An_in_context_edit_makes_changes_wait() => _run.CaseAsync(9, "busy with user_editing while a component is edited in context", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("placing", ".SLDASM");
        var editing = await Call<DiagEditComponentRequest, DiagEditComponentResult>(new DiagEditComponentRequest { Document = asm, Component = "base-1", Editing = true });
        Assert.NotNull(editing.Editing);
        try
        {
            var busy = await Refused(() => Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "base-1" }, Hidden = true }));
            Assert.Equal(ErrorCode.Busy, busy.Code);
            Assert.Equal("user_editing", busy.Error.Details!["reason"]);
            return $"editing {editing.Editing} in context: component.set answered busy ({busy.Error.Details["component"]})";
        }
        finally
        {
            await Call<DiagEditComponentRequest, DiagEditComponentResult>(new DiagEditComponentRequest { Document = asm, Editing = false });
        }
    });

    // Plan 4.B. refs.SLDASM: block-1 (40 x 20 x 10) fixed at the origin, and column-1 (10 x 10 x 30) turned a quarter
    // about z at (100, 0, 0), as case 3's post: its own x runs along the assembly's y and its own y along the
    // assembly's -x, so its box is x 90..100, y 0..10, z 0..30, and every position below proves the turn was applied.

    Task<ModelFindResult> FindFaces(string document, string? component, Vector3 direction) =>
        Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = document, Component = component, Kind = TopologyKind.Face, Direction = direction });

    Task<ModelMeasureResult> Measure(string assembly, string a, string b, string? aComponent = null, string? bComponent = null) =>
        Call<ModelMeasureRequest, ModelMeasureResult>(new ModelMeasureRequest { Document = assembly, A = a, AComponent = aComponent, B = b, BComponent = bComponent });

    static void Along(Vector3 expected, Vector3 actual, string what) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 1e-6 && Math.Abs(expected.Y - actual.Y) <= 1e-6 && Math.Abs(expected.Z - actual.Z) <= 1e-6,
            $"{what}: expected {expected}, got {actual}");

    [SkippableFact, Step(10)]
    public Task A_components_face_is_named_three_ways_in_the_assembly() => _run.CaseAsync(10, "a component's face by assembly reference and by {component, entity}; its planes by name@component; all in assembly coordinates", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string block = await ClosedBlock("block", 40, 20, 10);
        string column = await ClosedBlock("column", 10, 10, 30);
        string asm = await NewAssembly("refs");
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block });
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest
        {
            Document = asm, Path = column, At = new Point3(100, 0, 0), XAxis = new Vector3(0, 1, 0), ZAxis = new Vector3(0, 0, 1),
        });

        // The column's top face, found in the assembly: in assembly coordinates, naming its component, the assembly's reference.
        var top = Assert.Single((await FindFaces(asm, "column-1", new Vector3(0, 0, 1))).Entities);
        Assert.Equal("column-1", top.Component);
        Near(95, top.Origin!.X, "the column top's centre, x");
        Near(5, top.Origin.Y, "the column top's centre, y");
        Near(30, top.Origin.Z, "the column top's centre, z");
        Near(90, top.Box!.Min.X, "the column top's box, min x");
        // Its own -x side faces the assembly's -y: found by the turned direction, and not by its own.
        var side = Assert.Single((await FindFaces(asm, "column-1", new Vector3(0, -1, 0))).Entities);
        Near(0, side.Origin!.Y, "the column's own -x side, at assembly y");
        Assert.DoesNotContain((await FindFaces(asm, "column-1", new Vector3(-1, 0, 0))).Entities, e => Math.Abs(e.Origin!.X - 100) < Tol);

        // The same face as the column's own part names it, with its component: the same face, distance 0, same description.
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = column });
        var partTop = Assert.Single((await FindFaces(column, null, new Vector3(0, 0, 1))).Entities);
        Near(5, partTop.Origin!.X, "the top's centre in the column's own part, x");
        var tree = await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = column });
        string partFront = tree.Features.First(f => f.Kind == ReferenceKind.Plane).Reference!;
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = column });
        var same = await Measure(asm, top.Reference!, partTop.Reference!, bComponent: "column-1");
        Near(0, same.DistanceMm, "assembly reference to {component, entity}");
        Near(0, same.AngleDeg!.Value, "their angle");
        Assert.Equal(same.A.Origin!.ToString(), same.B.Origin!.ToString());

        // Planes by name@component, turned with the column: its own right plane's normal is the assembly's y.
        var right = await Measure(asm, "right@column-1", "top");
        Near(0, right.AngleDeg!.Value, "right@column-1 against the assembly's top");
        Assert.Equal("column-1", right.A.Component);
        var front = await Measure(asm, "front@column-1", partFront, bComponent: "column-1");
        Near(0, front.DistanceMm, "front@column-1 against the column's front plane by {component, entity}");
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"top of column-1 at {top.Origin} box {top.Box.Min}..{top.Box.Max}; its own -x side faces {side.Normal}; " +
               $"by {{component, entity}} {same.DistanceMm} mm away; right@column-1 {right.AngleDeg}° to top; front@column-1 {front.DistanceMm} mm from the part's front plane";
    });

    [SkippableFact, Step(11)]
    public Task An_assembly_reference_survives_and_a_suppressed_component_says_why() => _run.CaseAsync(11, "a face's assembly reference across a rebuild and a close and reopen; unresolved while its component is suppressed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("refs", ".SLDASM");
        string top = Assert.Single((await FindFaces(asm, "column-1", new Vector3(0, 0, 1))).Entities).Reference!;
        await Call<DocumentRebuildRequest, RebuildResult>(new DocumentRebuildRequest { Document = asm, Force = true });
        Near(30, (await Measure(asm, top, "top")).PointA!.Z, "after a rebuild");
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm });
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = asm });
        var reopened = await Measure(asm, top, "top");
        Near(30, reopened.PointA!.Z, "after a close and reopen");

        await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "column-1" }, Suppression = Suppression.Suppressed });
        try
        {
            var stale = await Refused(() => Measure(asm, top, "top"));
            Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
            Assert.Equal("component_suppressed", stale.Error.Details!["reason"]);
            var named = await Refused(() => Measure(asm, "front@column-1", "top"));
            Assert.Equal(ErrorCode.InvalidArgument, named.Code);
            Assert.Equal("suppressed", named.Error.Details!["state"]);
            var skipped = await FindFaces(asm, null, new Vector3(0, 0, 1));
            Assert.Contains(skipped.Skipped, s => s.Name == "column-1" && s.State == ComponentState.Suppressed);
            Assert.All(skipped.Entities, e => Assert.Equal("block-1", e.Component));
            await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "column-1" }, Suppression = Suppression.Resolved });
            Near(30, (await Measure(asm, top, "top")).PointA!.Z, "resolved again");
            return $"resolved after a rebuild and after a reopen ({reopened.A.Component}); suppressed: {stale.Error.Details["reason"]}; front@column-1: {named.Error.Details["state"]}; model.find skipped column-1";
        }
        finally
        {
            await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        }
    });

    [SkippableFact, Step(12)]
    public Task An_assembly_is_measured_weighed_and_boxed() => _run.CaseAsync(12, "model.measure between components; model.mass the sum of its parts; model.bounding_box", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("refs", ".SLDASM");
        var blockRight = (await FindFaces(asm, "block-1", new Vector3(1, 0, 0))).Entities.Single();
        var columnLeft = (await FindFaces(asm, "column-1", new Vector3(-1, 0, 0))).Entities.Single();
        var gap = await Measure(asm, blockRight.Reference!, columnLeft.Reference!);
        Near(50, gap.DistanceMm, "block-1's x = 40 face to column-1's x = 90 face");
        Near(0, gap.AngleDeg!.Value, "their angle");

        var mass = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = asm });
        Assert.Equal(new[] { "block-1", "column-1" }, mass.Components!.Select(c => c.Name));
        Near(11000, mass.VolumeMm3, "the assembly's volume");
        Near(8000, mass.Components![0].VolumeMm3, "block-1's volume");
        Near(3000, mass.Components![1].VolumeMm3, "column-1's volume");
        Near(mass.Components!.Sum(c => c.MassG), mass.MassG, "the components' masses add up");
        Near(95, mass.Components![1].CenterOfMass!.X, "column-1's centre of mass, x");
        Near(5, mass.Components![1].CenterOfMass!.Y, "column-1's centre of mass, y");
        Near(15, mass.Components![1].CenterOfMass!.Z, "column-1's centre of mass, z");

        var exact = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = asm, Precise = true });
        Near(0, exact.Box!.Min.X, "exact box min x");
        Near(100, exact.Box.Max.X, "exact box max x");
        Near(20, exact.Box.Max.Y, "exact box max y");
        Near(30, exact.Box.Max.Z, "exact box max z");
        var quick = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = asm });
        Assert.True(quick.Box!.Min.X <= Tol && quick.Box.Max.X >= 100 - Tol && quick.Box.Max.Z >= 30 - Tol, $"the quick box {quick.Box.Min}..{quick.Box.Max} does not hold the exact one");
        return $"gap {gap.DistanceMm} mm at {gap.AngleDeg}°; mass {mass.MassG} g = {string.Join(" + ", mass.Components!.Select(c => $"{c.Name} {c.MassG} g"))}, column-1's centre {mass.Components![1].CenterOfMass}; " +
               $"exact box {exact.Box.Min}..{exact.Box.Max}, quick {quick.Box.Min}..{quick.Box.Max}";
    });

    [SkippableFact, Step(13)]
    public Task An_assemblys_selection_is_reported() => _run.CaseAsync(13, "diag.select of a component's face, a component's plane and a component; selection.get and selection.changed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("refs", ".SLDASM");
        string top = Assert.Single((await FindFaces(asm, "column-1", new Vector3(0, 0, 1))).Entities).Reference!;
        long cursor = Sw.Events.LastSeq;
        var selected = await Call<DiagSelectRequest, SelectionResult>(new DiagSelectRequest { Document = asm, Items = new List<string> { top, "front@column-1", "block-1" } });
        Assert.Equal(new[] { "face", "plane", "component" }, selected.Items.Select(i => i.Type));
        var got = await Call<SelectionGetRequest, SelectionResult>(new SelectionGetRequest { Document = asm });
        Assert.Equal("column-1", got.Items[0].Entity!.Component);
        Near(30, got.Items[0].Entity!.Origin!.Z, "the selected top face's centre, z");
        Assert.Equal("column-1", got.Items[1].Entity!.Component);
        Assert.Equal(ReferenceKind.Component, got.Items[2].Entity!.Kind);
        EventEnvelope? changed = null;
        await LiveRun.WaitForAsync(() =>
        {
            changed = Sw.ReadEvents(new SwEventsRequest { AfterSeq = cursor, Kinds = new List<string> { "selection.changed" } }).Events.LastOrDefault();
            return Task.FromResult(changed != null && ((List<object?>)changed.Data!["items"]!).Count == 3);
        }, TimeSpan.FromSeconds(10), "selection.changed with the face, the plane and the component");
        return $"selected {string.Join(", ", got.Items.Select(i => $"{i.Type} {i.Entity!.Name ?? i.Entity.Feature} ({i.Entity.Component ?? "assembly"})"))}; selection.changed raised with 3 items";
    });

    [SkippableFact, Step(14)]
    public Task An_assemblys_configurations_are_listed_added_and_activated() => _run.CaseAsync(14, "configuration.list, add and activate on an assembly", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("refs", ".SLDASM");
        var list = await Call<ConfigurationListRequest, ConfigurationListResult>(new ConfigurationListRequest { Document = asm });
        string first = list.Active;
        var added = await Call<ConfigurationAddRequest, ConfigurationResult>(new ConfigurationAddRequest { Document = asm, Name = "Spare" });
        Assert.Equal("Spare", added.Active);
        Assert.Equal(DocumentType.Assembly, added.Change.Document.Type);
        Near(11000, added.Change.VolumeAfterMm3, "the evidence block's volume");
        var back = await Call<ConfigurationActivateRequest, ConfigurationResult>(new ConfigurationActivateRequest { Document = asm, Name = first });
        Assert.Equal(first, back.Active);
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{first} active; added Spare ({added.Change.BodiesAfter} bodies, {added.Change.VolumeAfterMm3} mm³); {first} active again; configurations {string.Join(", ", back.Configurations)}";
    });

    [SkippableFact, Step(15)]
    public Task A_component_found_in_another_folder_is_listed_and_its_references_refused() => _run.CaseAsync(15, "document.open lists a component SOLIDWORKS loaded from another folder; its old references are refused", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        // 4.A live run, case 7, round 2: a moved component file was loaded from a same-named file in a folder SOLIDWORKS
        // had opened that session. This makes that happen on purpose: the same name, saved in a second folder first.
        string name = "swap-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string other = Path.Combine(_run.Guard.ScratchDirectory, "elsewhere");
        Directory.CreateDirectory(other);
        string elsewhere = Path.Combine(other, name + ".SLDPRT");
        _run.Guard.Created(elsewhere);
        await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = elsewhere, XMm = 20, YMm = 20, ZMm = 20 });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = elsewhere });
        string meant = await ClosedBlock(name, 10, 10, 10);
        string asm = await NewAssembly("swap");
        var inserted = await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = meant });
        string top = Assert.Single((await FindFaces(asm, inserted.Component.Name, new Vector3(0, 0, 1))).Entities).Reference!;
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm });
        File.Move(meant, meant + ".moved");
        try
        {
            var opened = await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = asm });
            Assert.Null((await _run.AddinStateAsync()).Dialog);
            var swapped = Assert.Single(opened.Substituted);
            Assert.Equal(meant, swapped.Expected, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(elsewhere, swapped.Loaded, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(opened.Missing);
            var refused = await Refused(() => Measure(asm, top, "top"));
            Assert.Equal(ErrorCode.ReferenceUnresolved, refused.Code);
            Assert.Equal("component_file_changed", refused.Error.Details!["reason"]);
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm, Discard = true });
            return $"substituted: {swapped.Expected} -> {swapped.Loaded}; missing: none; no dialog; the reference issued before: {refused.Error.Details["reason"]}";
        }
        finally
        {
            File.Move(meant + ".moved", meant);
        }
    });

    [SkippableFact, Step(16)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(16, "the suite's documents closed", async () =>
    {
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        int closed = 0;
        // Assemblies first: they unload their hidden components; then whatever still has a window.
        foreach (var d in state.Documents.Where(d => d.Path != null && d.Visible).OrderBy(d => d.Type == DocumentType.Assembly ? 0 : 1))
        {
            if (!(await _run.AddinStateAsync()).Documents.Any(o => o.Path == d.Path)) continue;
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true });
            closed++;
        }
        var open = (await _run.AddinStateAsync()).Documents;
        Assert.True(open.Count == 0, $"still open after closing {closed}: {string.Join(", ", open.Select(d => d.Path ?? d.Title))}");
        return $"closed {closed} documents; none open";
    });

    string LiveScratchPath(string name, string extension) => Path.Combine(_run.Guard.ScratchDirectory, name + extension);
}
