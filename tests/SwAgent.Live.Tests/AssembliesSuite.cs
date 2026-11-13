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
/// precondition, cases 1 to 9; plan 4.B assembly references and inspection, cases 10 to 15; plan 4.C mates and degrees of freedom, cases 16 to 20; plan 4.D component patterns and interference, cases 21 to 26): every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly as swmcp serve does, in
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

    /// <summary>A Ø10 x 30 mm pin (mpin: case 5 already made pin.SLDPRT) standing on the front plane (axis z, z 0 to 30), saved in scratch and closed.</summary>
    async Task<string> ClosedPin(string name)
    {
        string path = New(name, ".SLDPRT");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Part, Path = path });
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path, On = "front", Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = new Point2(0, 0), RadiusMm = 5 } },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = path, Sketch = sketch.Sketch.Name, DepthMm = 30 });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = path });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path });
        return path;
    }

    /// <summary>A 40 x 20 x 10 mm block with a Ø10 hole through it at (20, 10), saved in scratch and closed.</summary>
    async Task<string> ClosedPlate(string name)
    {
        var block = await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = New(name, ".SLDPRT"), XMm = 40, YMm = 20, ZMm = 10 });
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = block.Path, On = "front", Circles = new List<SketchCircle> { new SketchCircle { Id = "h", Center = new Point2(20, 10), RadiusMm = 5 } },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = block.Path, Sketch = sketch.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = block.Path });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = block.Path });
        return block.Path!;
    }

    async Task<EntityInfo> PlaneFace(string asm, string component, Vector3 direction) =>
        Assert.Single((await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest
        {
            Document = asm, Component = component, Kind = TopologyKind.Face, Geometry = GeometryType.Plane, Direction = direction,
        })).Entities);

    async Task<EntityInfo> CylinderFace(string asm, string component) =>
        Assert.Single((await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest
        {
            Document = asm, Component = component, Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder,
        })).Entities);

    Task<MateAddResult> Mate(string asm, MateType type, string a, string b, double? distance = null, double? angle = null) =>
        Call<MateAddRequest, MateAddResult>(new MateAddRequest
        {
            Document = asm, Type = type, Entities = { new MateEntity { Entity = a }, new MateEntity { Entity = b } }, DistanceMm = distance, AngleDeg = angle,
        });

    static void Holds(MateAddResult r) =>
        Assert.All(r.Measured, m => Assert.True(Math.Abs(m.Measured - m.Expected) <= m.Tolerance, $"{r.Mate.Name} {m.Condition}: {m.Measured}, expected {m.Expected}"));

    static readonly Vector3 Up = new Vector3(0, 0, 1), Down = new Vector3(0, 0, -1), Front = new Vector3(0, -1, 0), Left = new Vector3(-1, 0, 0);

    /// <summary>The distance from a point to the line through origin along axis, mm (the suite's own arithmetic, independent of the add-in's).</summary>
    static double LineOffset(Point3 origin, Vector3 axis, Point3 point)
    {
        double dx = point.X - origin.X, dy = point.Y - origin.Y, dz = point.Z - origin.Z;
        double cx = dy * axis.Z - dz * axis.Y, cy = dz * axis.X - dx * axis.Z, cz = dx * axis.Y - dy * axis.X;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz) / Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
    }

    async Task<Dictionary<string, Transform>> Placements(string asm) =>
        (await Components(asm, recursive: true)).Components.ToDictionary(c => c.Name, c => c.Transform);

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
        // A forced rebuild leaves the assembly and its parts dirty; save, so the close below unloads them without losing anything.
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
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
    public Task An_assembly_is_measured_weighed_and_boxed() => _run.CaseAsync(12, "model.measure between components; model.mass the sum of its parts, also with a component in another configuration; model.bounding_box", async () =>
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
        Assert.Empty(mass.Skipped!);

        // Review of 4.B: a share counts the configuration the assembly uses, not the file's active one. column.SLDPRT
        // gets a Tall configuration 50 mm high, its file left in Default (30 mm), and column-1 is set to Tall.
        string column = (await Component(asm, "column-1")).Path!;
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = column });
        string first = (await Call<ConfigurationListRequest, ConfigurationListResult>(new ConfigurationListRequest { Document = column })).Active;
        var height = (await Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = column }))
            .Dimensions.Single(d => d.Driving && Math.Abs(d.Value - 30) < Tol);
        await Call<ConfigurationAddRequest, ConfigurationResult>(new ConfigurationAddRequest { Document = column, Name = "Tall" });
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = column, Dimension = height.Name, Value = 50 });
        await Call<ConfigurationActivateRequest, ConfigurationResult>(new ConfigurationActivateRequest { Document = column, Name = first });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = column });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = column });
        ModelMassResult tall;
        await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "column-1" }, Configuration = "Tall" });
        try
        {
            tall = await Call<ModelMassRequest, ModelMassResult>(new ModelMassRequest { Document = asm });
            Assert.Empty(tall.Skipped!);
            Near(13000, tall.VolumeMm3, "the assembly's volume with column-1 in Tall");
            Near(5000, tall.Components![1].VolumeMm3, "column-1's volume in Tall, not its file's active 3000");
            Near(tall.Components!.Sum(c => c.MassG), tall.MassG, "the components' masses add up with column-1 in Tall");
            Near(95, tall.Components![1].CenterOfMass!.X, "column-1's centre of mass in Tall, x");
            Near(25, tall.Components![1].CenterOfMass!.Z, "column-1's centre of mass in Tall, z");
        }
        finally
        {
            await Call<ComponentSetRequest, ComponentSetResult>(new ComponentSetRequest { Document = asm, Components = { "column-1" }, Configuration = first });
            await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        }

        var exact = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = asm, Precise = true });
        Near(0, exact.Box!.Min.X, "exact box min x");
        Near(100, exact.Box.Max.X, "exact box max x");
        Near(20, exact.Box.Max.Y, "exact box max y");
        Near(30, exact.Box.Max.Z, "exact box max z");
        var quick = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = asm });
        Assert.True(quick.Box!.Min.X <= Tol && quick.Box.Max.X >= 100 - Tol && quick.Box.Max.Z >= 30 - Tol, $"the quick box {quick.Box.Min}..{quick.Box.Max} does not hold the exact one");
        return $"gap {gap.DistanceMm} mm at {gap.AngleDeg}°; mass {mass.MassG} g = {string.Join(" + ", mass.Components!.Select(c => $"{c.Name} {c.MassG} g"))}, column-1's centre {mass.Components![1].CenterOfMass}; " +
               $"column-1 in Tall: {tall.MassG} g = {string.Join(" + ", tall.Components!.Select(c => $"{c.Name} {c.VolumeMm3} mm³"))}, centre {tall.Components![1].CenterOfMass}; " +
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
            // SOLIDWORKS does not always search: 4.A's round 2 loaded the same-named file from elsewhere\, 4.B's round 2
            // reported it missing. Either outcome is accepted, each checked strictly; the line says which one ran.
            string branch;
            SwAgentException refused;
            if (opened.Substituted.Count > 0)
            {
                var swapped = Assert.Single(opened.Substituted);
                Assert.Equal(meant, swapped.Expected, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(elsewhere, swapped.Loaded, StringComparer.OrdinalIgnoreCase);
                Assert.Empty(opened.Missing);
                refused = await Refused(() => Measure(asm, top, "top"));
                Assert.Equal(ErrorCode.ReferenceUnresolved, refused.Code);
                Assert.Equal("component_file_changed", refused.Error.Details!["reason"]);
                branch = $"branch substituted: {swapped.Expected} -> {swapped.Loaded}; missing: none";
            }
            else
            {
                Assert.Equal(meant, Assert.Single(opened.Missing), StringComparer.OrdinalIgnoreCase);
                refused = await Refused(() => Measure(asm, top, "top"));
                Assert.Equal(ErrorCode.ReferenceUnresolved, refused.Code);
                Assert.Equal("component_missing", refused.Error.Details!["reason"]);
                branch = $"branch missing (SOLIDWORKS did not load elsewhere\\{name}.SLDPRT): missing: {opened.Missing[0]}; substituted: none";
            }
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = asm, Discard = true });
            return $"{branch}; no dialog; the reference issued before: {refused.Error.Details!["reason"]}";
        }
        finally
        {
            File.Move(meant + ".moved", meant);
        }
    });

    [SkippableFact, Step(16)]
    public Task Concentric_coincident_parallel_and_distance_hold_as_measured() => _run.CaseAsync(16, "mate.add concentric, coincident, parallel, distance: each measured, the solve's moves reported", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string plate = await ClosedPlate("plate");
        string pin = await ClosedPin("mpin");
        string block = await ClosedBlock("mblock", 40, 20, 10);
        string asm = await NewAssembly("mates");
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = plate });
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(60, 0, 0) });
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(0, 40, 0) });
        string plateC = Path.GetFileNameWithoutExtension(plate) + "-1", pinC = Path.GetFileNameWithoutExtension(pin) + "-1", blockC = Path.GetFileNameWithoutExtension(block) + "-1";

        var hole = await CylinderFace(asm, plateC);
        var concentric = await Mate(asm, MateType.Concentric, (await CylinderFace(asm, pinC)).Reference!, hole.Reference!);
        Holds(concentric);
        Assert.Contains(concentric.Change.Moved, m => m.Name == pinC);
        var side = await CylinderFace(asm, pinC);
        Near(0, LineOffset(hole.Origin!, hole.Axis!, side.Origin!), "the pin's axis from the hole's, read again");

        var flush = await Mate(asm, MateType.Coincident, (await PlaneFace(asm, pinC, Up)).Reference!, (await PlaneFace(asm, plateC, Up)).Reference!);
        Holds(flush);
        Near(10, (await PlaneFace(asm, pinC, Up)).Origin!.Z, "the pin's top, flush with the plate's");

        var seat = await Mate(asm, MateType.Coincident, (await PlaneFace(asm, blockC, Down)).Reference!, (await PlaneFace(asm, plateC, Up)).Reference!);
        Holds(seat);
        var square = await Mate(asm, MateType.Parallel, (await PlaneFace(asm, blockC, Front)).Reference!, (await PlaneFace(asm, plateC, Front)).Reference!);
        Holds(square);
        var gap = await Mate(asm, MateType.Distance, (await PlaneFace(asm, blockC, Left)).Reference!, (await PlaneFace(asm, plateC, Left)).Reference!, distance: 15);
        Holds(gap);
        Near(15, Math.Abs((await PlaneFace(asm, blockC, Left)).Origin!.X), "the block's left face, read again");
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{concentric.Mate.Name} {concentric.Mate.Alignment} moved {string.Join(", ", concentric.Change.Moved.Select(m => m.Name))}; " +
               $"{flush.Mate.Name} {flush.Mate.Alignment}; {seat.Mate.Name}; {square.Mate.Name}; {gap.Mate.Name} {gap.Measured[0].Measured} mm";
    });

    [SkippableFact, Step(17)]
    public Task Perpendicular_angle_tangent_and_lock_with_edit_and_delete() => _run.CaseAsync(17, "perpendicular then deleted; angle 30 edited to 45, suppressed and back; tangent; lock fully defines", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("mates", ".SLDASM");
        string block = LiveScratchPath("mblock", ".SLDPRT"), pin = LiveScratchPath("mpin", ".SLDPRT");
        var b2 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(0, -60, 0) })).Component.Name;
        var p2 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(80, 0, 20) })).Component.Name;
        var b3 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(0, 100, 0) })).Component.Name;
        string plateC = "plate-1";

        var perpendicular = await Mate(asm, MateType.Perpendicular, (await PlaneFace(asm, b2, Up)).Reference!, (await PlaneFace(asm, plateC, Front)).Reference!);
        Holds(perpendicular);
        var before = await Placements(asm);
        var deleted = await Call<MateDeleteRequest, MateDeleteResult>(new MateDeleteRequest { Document = asm, Mates = { perpendicular.Mate.Reference! } });
        Assert.Contains(b2, deleted.Stayed);
        Assert.Empty(deleted.Change.Moved);
        var after = await Placements(asm);
        Near(before[b2].Origin.Y, after[b2].Origin.Y, "the block stays where the deleted mate left it");

        var angle = await Mate(asm, MateType.Angle, (await PlaneFace(asm, b2, Front)).Reference!, (await PlaneFace(asm, plateC, Front)).Reference!, angle: 30);
        Holds(angle);
        var edited = await Call<MateEditRequest, MateEditResult>(new MateEditRequest { Document = asm, Mate = angle.Mate.Reference!, AngleDeg = 45 });
        Assert.All(edited.Measured, m => Assert.True(Math.Abs(m.Measured - m.Expected) <= m.Tolerance, $"{m.Condition} {m.Measured}"));
        Near(45, edited.Mate.AngleDeg!.Value, "the angle read back");
        Assert.True((await Call<MateEditRequest, MateEditResult>(new MateEditRequest { Document = asm, Mate = angle.Mate.Name, Suppressed = true })).Mate.Suppressed);
        Assert.False((await Call<MateEditRequest, MateEditResult>(new MateEditRequest { Document = asm, Mate = angle.Mate.Name, Suppressed = false })).Mate.Suppressed);

        var tangent = await Mate(asm, MateType.Tangent, (await CylinderFace(asm, p2)).Reference!, (await PlaneFace(asm, plateC, Up)).Reference!);
        Holds(tangent);
        var locked = await Mate(asm, MateType.Lock, b3, plateC);
        Holds(locked);
        Assert.Equal(Constraint.FullyDefined, locked.Change.Constraints.Single(c => c.Name == b3).Constraint);
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{perpendicular.Mate.Name} 90° then deleted ({b2} stayed); {angle.Mate.Name} {angle.Measured[0].Measured}° then {edited.Measured[0].Measured}°, suppressed and back; " +
               $"{tangent.Mate.Name} {tangent.Measured[0].Measured} mm; {locked.Mate.Name} {b3} fully defined";
    });

    [SkippableFact, Step(18)]
    public Task Degrees_of_freedom_are_what_the_mates_leave() => _run.CaseAsync(18, "assembly.dof: fixed, fully defined, one coincident face (2 + 1), a concentric pin (1 + 1), the flush pin (0 + 1); mate.list by component", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("mates", ".SLDASM");
        string block = LiveScratchPath("mblock", ".SLDPRT"), pin = LiveScratchPath("mpin", ".SLDPRT");
        var b4 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(0, 140, 0) })).Component.Name;
        var p3 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(100, 0, 0) })).Component.Name;
        Holds(await Mate(asm, MateType.Coincident, (await PlaneFace(asm, b4, Down)).Reference!, (await PlaneFace(asm, "plate-1", Up)).Reference!));
        Holds(await Mate(asm, MateType.Concentric, (await CylinderFace(asm, p3)).Reference!, (await CylinderFace(asm, "plate-1")).Reference!));
        var dof = (await Call<AssemblyDofRequest, AssemblyDofResult>(new AssemblyDofRequest { Document = asm })).Components.ToDictionary(c => c.Name);
        Assert.Equal(Constraint.Fixed, dof["plate-1"].Constraint);
        Assert.Equal(Constraint.FullyDefined, dof["mblock-3"].Constraint);
        string Counts(ComponentFreedom f) => $"{f.Constraint} {f.FreeTranslations?.ToString() ?? "?"}+{f.FreeRotations?.ToString() ?? "?"}{(f.Note == null ? "" : $" ({f.Note})")}";
        string report = $"plate-1 {Counts(dof["plate-1"])}; mblock-3 {Counts(dof["mblock-3"])}; {b4} {Counts(dof[b4])}; {p3} {Counts(dof[p3])}; mpin-1 {Counts(dof["mpin-1"])}";
        // The gap branch (plan 4.C, Task 14): SOLIDWORKS 2026 SP4 answered GetRemainingDOFs Unavailable for mated
        // components, so the constrained state is asserted always, and the counts only where SOLIDWORKS gave them.
        void Free(ComponentFreedom f, int translations, int rotations, string what)
        {
            Assert.True(f.Constraint == Constraint.UnderDefined, $"{what}: {report}");
            if (f.FreeTranslations == null || f.FreeRotations == null) Assert.False(string.IsNullOrEmpty(f.Note), $"{what}: no counts and no note: {report}");
            else Assert.True(f.FreeTranslations == translations && f.FreeRotations == rotations, $"{what}: {report}");
        }
        Free(dof[b4], 2, 1, "one coincident face");
        Free(dof[p3], 1, 1, "a concentric pin");
        Free(dof["mpin-1"], 0, 1, "the flush pin");
        var pinMates = await Call<MateListRequest, MateListResult>(new MateListRequest { Document = asm, Component = "mpin-1" });
        Assert.Equal(2, pinMates.Mates.Count);
        Assert.All(pinMates.Mates, m => Assert.Contains(m.Entities, e => e.Component == "mpin-1"));
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return report + $"; mpin-1's mates {string.Join(", ", pinMates.Mates.Select(m => $"{m.Name} ({m.Type})"))}";
    });

    [SkippableFact, Step(19)]
    public Task An_over_defining_mate_is_rolled_back_with_every_component_back() => _run.CaseAsync(19, "a distance mate fighting the flush coincident: refused, the mate gone, every component where it was", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("mates", ".SLDASM");
        var before = await Placements(asm);
        int mates = (await Call<MateListRequest, MateListResult>(new MateListRequest { Document = asm })).Mates.Count;
        string pinTop = (await PlaneFace(asm, "mpin-1", Up)).Reference!, plateTop = (await PlaneFace(asm, "plate-1", Up)).Reference!;
        var refused = await Refused(() => Mate(asm, MateType.Distance, pinTop, plateTop, distance: 5));
        Assert.True(refused.Code == ErrorCode.OperationFailed || refused.Code == ErrorCode.VerificationFailed, $"{refused.Code}: {refused.Message}");
        Assert.False(refused.Error.Details != null && refused.Error.Details.TryGetValue("rollback_failed", out var rf) && rf is true, "the rollback itself failed: " + refused.Message);
        Assert.Equal(mates, (await Call<MateListRequest, MateListResult>(new MateListRequest { Document = asm })).Mates.Count);
        var after = await Placements(asm);
        foreach (var (name, was) in before)
        {
            Near(was.Origin.X, after[name].Origin.X, $"{name} x after the rollback");
            Near(was.Origin.Y, after[name].Origin.Y, $"{name} y after the rollback");
            Near(was.Origin.Z, after[name].Origin.Z, $"{name} z after the rollback");
            Along(was.ZAxis, after[name].ZAxis, $"{name} z axis after the rollback");
        }
        string restored = "none needed";
        if (refused.Error.Details != null && refused.Error.Details.TryGetValue("positions_restored", out var p) && p is List<object?> put) restored = string.Join(", ", put);
        return $"{WireNames.ForEnum(refused.Code)}: {refused.Message}; components put back: {restored}; all {before.Count} where they were";
    });

    [SkippableFact, Step(20)]
    public Task A_mates_reference_goes_stale_with_its_component() => _run.CaseAsync(20, "component.delete takes a component's mates; their references are reference_unresolved", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("mates", ".SLDASM");
        var mate = Assert.Single((await Call<MateListRequest, MateListResult>(new MateListRequest { Document = asm, Component = "mblock-4" })).Mates);
        var gone = await Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { "mblock-4" } });
        Assert.Contains(mate.Name, gone.MatesDeleted);
        var stale = await Refused(() => Call<MateEditRequest, MateEditResult>(new MateEditRequest { Document = asm, Mate = mate.Reference!, Suppressed = true }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{mate.Name} went with mblock-4; its reference: {stale.Error.Details?["reason"]}";
    });

    // Plan 4.D. patterns.SLDASM: pplate-1 (80 x 80 x 10, a Ø10 hole at its centre (40, 40), and its circular pattern of
    // four Ø10 holes at (60, 40), (40, 60), (20, 40) and (40, 20)) fixed at the origin; lcube-1 (10 x 10 x 10) the linear
    // seed; mpin (case 16's Ø10 x 30 pin, axis z, z 0 to 30) the circular and feature-driven seeds.

    /// <summary>The pattern plate: a centre hole, a hole at (60, 40), and that hole patterned 4 times round the centre hole; saved in scratch and closed.</summary>
    async Task<string> ClosedPatternPlate(string name)
    {
        var block = await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = New(name, ".SLDPRT"), XMm = 80, YMm = 80, ZMm = 10 });
        string cut = "";
        foreach (var (id, x) in new[] { ("c", 40.0), ("h", 60.0) })
        {
            var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
            {
                Document = block.Path, On = "front", Circles = new List<SketchCircle> { new SketchCircle { Id = id, Center = new Point2(x, 40), RadiusMm = 5 } },
            });
            cut = (await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = block.Path, Sketch = sketch.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll })).Feature.Name!;
        }
        var centre = (await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest
        {
            Document = block.Path, Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder, Near = new Point3(40, 40, 5), MaxDistanceMm = 6, Limit = 1,
        })).Entities.Single();
        await Call<FeatureCircularPatternRequest, FeatureResult>(new FeatureCircularPatternRequest { Document = block.Path, Features = new List<string> { cut }, Axis = centre.Reference!, Count = 4 });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = block.Path });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = block.Path });
        return block.Path!;
    }

    async Task<EntityInfo> HoleAt(string asm, string component, double x, double y) =>
        (await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest
        {
            Document = asm, Component = component, Kind = TopologyKind.Face, Geometry = GeometryType.Cylinder, Near = new Point3(x, y, 5), MaxDistanceMm = 6, Limit = 1,
        })).Entities.Single();

    static bool At(Point3 p, double x, double y, double z) => Math.Abs(p.X - x) <= Tol && Math.Abs(p.Y - y) <= Tol && Math.Abs(p.Z - z) <= Tol;

    /// <summary>Every instance of the pattern sits on one of the places, each place taken once (the suite's own check, independent of the add-in's).</summary>
    static void Lands(ComponentPatternResult r, Dictionary<string, Transform> placements, IReadOnlyList<Point3> places)
    {
        Assert.Equal(places.Count, r.Pattern.Instances.Count);
        var free = places.ToList();
        foreach (var name in r.Pattern.Instances)
        {
            var o = placements[name].Origin;
            var hit = free.FirstOrDefault(p => At(o, p.X, p.Y, p.Z));
            Assert.True(hit != null, $"{name} at {o} is on none of the places left: {string.Join(" ", free)}");
            free.Remove(hit!);
        }
    }

    Task<AssemblyInterferenceResult> Interference(string asm, bool coincident = false, params string[] components) =>
        Call<AssemblyInterferenceRequest, AssemblyInterferenceResult>(new AssemblyInterferenceRequest
        {
            Document = asm, CoincidentIsInterference = coincident, Components = components.Length == 0 ? null : components.ToList(),
        });

    [SkippableFact, Step(21)]
    public Task A_linear_component_pattern_lands_every_instance() => _run.CaseAsync(21, "component.linear_pattern: a 3 x 2 grid of cubes, each where the spacing puts it", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string plate = await ClosedPatternPlate("pplate");
        string cube = await ClosedBlock("lcube", 10, 10, 10);
        string asm = await NewAssembly("patterns");
        await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = plate });
        string seed = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = cube, At = new Point3(0, -40, 0) })).Component.Name;
        var right = await PlaneFace(asm, "pplate-1", new Vector3(1, 0, 0));
        var front = await PlaneFace(asm, "pplate-1", Front);
        var made = await Call<ComponentLinearPatternRequest, ComponentPatternResult>(new ComponentLinearPatternRequest
        {
            Document = asm, Components = { seed }, Direction = right.Reference!, SpacingMm = 20, Count = 3,
            Direction2 = new ComponentPatternDirection { Direction = front.Reference!, SpacingMm = 15, Count = 2 },
        });
        Assert.All(made.Instances, i => Assert.True(i.OffsetMm <= Tol, $"{i.Name} {i.OffsetMm} mm off"));
        Assert.Equal(new[] { seed }, made.Pattern.Seeds);
        int sx = made.Sense!.StartsWith("along", StringComparison.Ordinal) ? 1 : -1, sy = made.Sense.EndsWith("/along", StringComparison.Ordinal) ? 1 : -1;
        var places = new List<Point3>();
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                if (i + j > 0) places.Add(new Point3(sx * 20 * i, -40 - sy * 15 * j, 0));
            }
        }
        Lands(made, await Placements(asm), places);
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{made.Pattern.Name}: {made.Instances.Count} instances, sense {made.Sense}, worst offset {made.Instances.Max(i => i.OffsetMm)} mm; created {made.Change.Created.Single().Name}";
    });

    [SkippableFact, Step(22)]
    public Task A_circular_component_pattern_turns_every_instance() => _run.CaseAsync(22, "component.circular_pattern: 4 over 360 and 3 over 180 round the centre hole", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("patterns", ".SLDASM"), pin = LiveScratchPath("mpin", ".SLDPRT");
        var centre = await HoleAt(asm, "pplate-1", 40, 40);
        string p1 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(70, 40, 20) })).Component.Name;
        var four = await Call<ComponentCircularPatternRequest, ComponentPatternResult>(new ComponentCircularPatternRequest { Document = asm, Components = { p1 }, Axis = centre.Reference!, Count = 4 });
        Assert.Equal(new[] { p1 }, four.Pattern.Seeds);
        Lands(four, await Placements(asm), new[] { new Point3(40, 70, 20), new Point3(10, 40, 20), new Point3(40, 10, 20) });
        Assert.All(four.Instances, i => Assert.True((i.TurnDeg ?? 0) <= 0.01, $"{i.Name} turned {i.TurnDeg}° off"));
        string p2 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(70, 40, 50) })).Component.Name;
        var half = await Call<ComponentCircularPatternRequest, ComponentPatternResult>(new ComponentCircularPatternRequest { Document = asm, Components = { p2 }, Axis = centre.Reference!, Count = 3, AngleDeg = 180 });
        Assert.Equal(new[] { p2 }, half.Pattern.Seeds);
        // The sense is the turn about the centre hole's axis as SOLIDWORKS gives it, which may point down (-Z) as well as up.
        bool up = centre.Axis!.Z > 0;
        double y = (half.Sense == "along") == up ? 70 : 10;
        Lands(half, await Placements(asm), new[] { new Point3(40, y, 50), new Point3(10, 40, 50) });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{four.Pattern.Name}: 3 instances at 90° steps, sense {four.Sense}; {half.Pattern.Name}: 180° over 3 is 90° steps, sense {half.Sense} about an axis pointing {(up ? "+Z" : "-Z")}";
    });

    [SkippableFact, Step(23)]
    public Task A_feature_driven_pattern_puts_a_pin_in_every_hole() => _run.CaseAsync(23, "component.feature_pattern: one pin becomes one in each of the plate's four pattern holes", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("patterns", ".SLDASM"), pin = LiveScratchPath("mpin", ".SLDPRT"), plate = LiveScratchPath("pplate", ".SLDPRT");
        string pattern = (await Call<ModelTreeRequest, ModelTreeResult>(new ModelTreeRequest { Document = plate })).Features.Single(f => f.Type == "CirPattern").Name;
        string seed = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = pin, At = new Point3(60, 40, 0) })).Component.Name;
        var driven = await Call<ComponentFeaturePatternRequest, ComponentPatternResult>(new ComponentFeaturePatternRequest { Document = asm, Components = { seed }, DrivingFeature = $"{pattern}@pplate-1" });
        Assert.Equal(4, driven.Holes);
        Assert.Equal(new[] { seed }, driven.Pattern.Seeds);
        Lands(driven, await Placements(asm), new[] { new Point3(40, 60, 0), new Point3(20, 40, 0), new Point3(40, 20, 0) });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"{driven.Pattern.Name} driven by {pattern}@pplate-1: {driven.Holes} holes, seed {string.Join(", ", driven.Pattern.Seeds)}, instances {string.Join(", ", driven.Pattern.Instances)}";
    });

    [SkippableFact, Step(24)]
    public Task A_pattern_is_deleted_by_its_feature_and_its_seed_only_with_it() => _run.CaseAsync(24, "component.delete: a lone seed and a lone instance refused; the pattern and its seed deleted together", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("patterns", ".SLDASM");
        var at = await Placements(asm);
        string seed = at.Single(p => At(p.Value.Origin, 60, 40, 0)).Key, instance = at.Single(p => At(p.Value.Origin, 40, 60, 0)).Key;
        var lone = await Refused(() => Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { seed } }));
        Assert.Equal(ErrorCode.InvalidArgument, lone.Code);
        string pattern = (string)Assert.Single((List<object?>)lone.Error.Details!["patterns"]!)!;
        var one = await Refused(() => Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { instance } }));
        Assert.Equal(pattern, one.Error.Details!["pattern"]);
        var gone = await Call<ComponentDeleteRequest, ComponentDeleteResult>(new ComponentDeleteRequest { Document = asm, Components = { pattern, seed } });
        Assert.Equal(new[] { pattern }, gone.PatternsDeleted);
        Assert.Equal(3, gone.InstancesDeleted.Count);
        Assert.Equal(new[] { seed }, gone.Deleted);
        var after = await Placements(asm);
        Assert.DoesNotContain(after, p => At(p.Value.Origin, 40, 60, 0) || At(p.Value.Origin, 60, 40, 0));
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        return $"lone seed refused naming {pattern}; lone instance refused; deleted {pattern} with {string.Join(", ", gone.InstancesDeleted)} and its seed {seed}";
    });

    [SkippableFact, Step(25)]
    public Task Interference_is_the_overlap_volume_and_touching_counts_only_when_asked() => _run.CaseAsync(25, "assembly.interference: blocks overlapping 10 x 20 x 10 report 2000 mm³; touching faces none unless asked", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string block = await ClosedBlock("iblock", 40, 20, 10);
        string asm = await NewAssembly("clash");
        string b1 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block })).Component.Name;
        string b2 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(30, 0, 0) })).Component.Name;
        string b3 = (await Call<AssemblyInsertRequest, AssemblyInsertResult>(new AssemblyInsertRequest { Document = asm, Path = block, At = new Point3(0, 20, 0) })).Component.Name;
        var plain = await Interference(asm);
        var overlap = Assert.Single(plain.Interferences);
        Assert.Equal(new[] { b1, b2 }.OrderBy(n => n), overlap.Components.OrderBy(n => n));
        Assert.True(Math.Abs(overlap.VolumeMm3 - 2000) <= 0.01, $"volume {overlap.VolumeMm3} mm³");
        Assert.NotNull(overlap.Box);
        Near(30, overlap.Box!.Min.X, "box min x");
        Near(40, overlap.Box.Max.X, "box max x");
        Near(20, overlap.Box.Max.Y, "box max y");
        Near(10, overlap.Box.Max.Z, "box max z");
        var touching = await Interference(asm, coincident: true);
        Assert.Contains(touching.Interferences, i => i.Components.Contains(b3));
        var onlyB2 = await Interference(asm, false, b2);
        Assert.Equal(2000, Math.Round(Assert.Single(onlyB2.Interferences).VolumeMm3, 2));
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = asm });
        var t = touching.Interferences.First(i => i.Components.Contains(b3));
        return $"{b1}/{b2}: {overlap.VolumeMm3} mm³, box {overlap.Box.Min} to {overlap.Box.Max}; touching {b3}: not reported by default, with coincident {t.VolumeMm3} mm³ touching={t.Touching}; components=[{b2}] keeps 1";
    });

    [SkippableFact, Step(26)]
    public Task The_interference_session_is_closed_after_a_timeout() => _run.CaseAsync(26, "assembly.interference held past its deadline: timeout, then operation.completed, the session closed (Done)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string asm = LiveScratchPath("clash", ".SLDASM");
        var before = await Call<DiagInterferenceRequest, DiagInterferenceResult>(new DiagInterferenceRequest { DelayMs = 3000 });
        Assert.Equal(3000, before.ArmedMs);
        long cursor = Sw.Events.LastSeq;
        var late = await Refused(() => Sw.CallAsync<AssemblyInterferenceRequest, AssemblyInterferenceResult>(new AssemblyInterferenceRequest { Document = asm }, TimeSpan.FromMilliseconds(1000)));
        Assert.Equal(ErrorCode.Timeout, late.Code);
        EventEnvelope? completed = null;
        await LiveRun.WaitForAsync(() =>
        {
            completed = Sw.ReadEvents(new SwEventsRequest { AfterSeq = cursor, Kinds = new List<string> { "operation.completed" } })
                .Events.FirstOrDefault(e => (string?)e.Data["method"] == "assembly.interference" && (string?)e.Data["answered_as"] == "timeout");
            return Task.FromResult(completed != null);
        }, TimeSpan.FromSeconds(15), "operation.completed for the assembly.interference answered timeout");
        Assert.Equal(true, completed!.Data["ok"]);
        var after = await Call<DiagInterferenceRequest, DiagInterferenceResult>(new DiagInterferenceRequest());
        Assert.Equal(before.Opened + 1, after.Opened);
        Assert.Equal(after.Opened, after.Closed);
        Assert.Equal(0, after.ArmedMs);
        Assert.Single((await Interference(asm)).Interferences);
        return $"timeout after 1000 ms with the session held 3000 ms; operation.completed ok after {completed.Data["duration_ms"]} ms; sessions opened {after.Opened}, closed {after.Closed}; the next call answered normally";
    });

    [SkippableFact, Step(27)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(27, "the suite's documents closed", async () =>
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
