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
/// precondition): every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly as swmcp serve does, in
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

    [SkippableFact, Step(10)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(10, "the suite's documents closed", async () =>
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
