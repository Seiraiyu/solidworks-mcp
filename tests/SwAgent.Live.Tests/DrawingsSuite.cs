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

/// <summary>The drawings suite's run: its own id (…-drawings-suite), directory, scratch directory and report.</summary>
public sealed class DrawingsSuiteRun : LiveRun
{
    public DrawingsSuiteRun()
        : base("drawings-suite")
    {
    }
}

/// <summary>
/// The drawings live suite (design 611, Testing; plan 5.A covers drawing documents, session preferences, sheets and
/// model and projected views, plan 5.B sections, details and flat-pattern views in cases 15 to 19, plan 5.C properties,
/// notes, imported and reference dimensions, centre marks and lines, datums and tolerance frames in cases 20 to 27 and
/// the user editing the sheet format or a view's sketch, by hand, in cases 28 and 29): every case drives seiraiyu2's SOLIDWORKS through SwAgent.Client exactly as swmcp serve
/// does, in [Step] order, and checks against exact values. It builds its own model first, a 60 x 40 x 20 mm block (x 0..60,
/// y 0..40, z 0..20) with a Ø10 mm hole through it along z at (20, 20), and draws it on an A3 sheet at 1:1 in third-angle
/// projection, so every view's drawn extents are known to 0.01 mm. It records each document before creating it, never
/// ends SOLIDWORKS, stops the run if a document it did not create is open, and closes everything it made at the end.
/// Skipped unless SWMCP_LIVE=1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class DrawingsSuite : IClassFixture<DrawingsSuiteRun>
{
    /// <summary>Sheet positions and extents agree within this, mm (design 611, How a view is checked).</summary>
    const double Tol = 0.01;

    static readonly string[] DrawingPreferences =
    {
        "insert_view_for_new_drawing", "drawing_show_sheet_format_dialog", "detailing_auto_insert_center_marks_for_holes",
        "detailing_auto_insert_center_marks_for_fillets", "detailing_auto_insert_center_marks_for_slots", "detailing_auto_insert_center_lines",
        "detailing_auto_insert_balloons", "detailing_auto_insert_dims_marked_for_drawing", "automatic_scaling_3_view_drawings",
    };

    readonly LiveRun _run;

    public DrawingsSuite(DrawingsSuiteRun run)
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

    static void Box(SheetBox? box, double minX, double maxX, double minY, double maxY, string what)
    {
        Assert.True(box != null, $"{what}: nothing drawn");
        Near(minX, box!.Min.X, what + " min x");
        Near(maxX, box.Max.X, what + " max x");
        Near(minY, box.Min.Y, what + " min y");
        Near(maxY, box.Max.Y, what + " max y");
    }

    /// <summary>A scratch path for a new document, recorded as this run's before anything creates it.</summary>
    string New(string name, string extension)
    {
        string path = _run.Guard.NewScratchPath(name, extension);
        _run.Guard.Created(path);
        return path;
    }

    string Block => Path.Combine(_run.Guard.ScratchDirectory, "block.SLDPRT");

    string Drawing => Path.Combine(_run.Guard.ScratchDirectory, "block.SLDDRW");

    Task<DrawingReadResult> Read() => Call<DrawingReadRequest, DrawingReadResult>(new DrawingReadRequest { Document = Drawing });

    async Task<ViewInfo> View(string name) => (await Read()).Sheets.SelectMany(s => s.Views).Single(v => v.Name == name);

    /// <summary>The views cases 5 to 13 use, by role, as SOLIDWORKS named them.</summary>
    static readonly Dictionary<string, string> Views = new Dictionary<string, string>(StringComparer.Ordinal);

    [SkippableFact, Step(1)]
    public Task SOLIDWORKS_is_ready_with_nothing_of_the_owners_open() => _run.CaseAsync(1, "SOLIDWORKS ready, only the suite's documents open, diagnostics on, drawing preferences applied", async () =>
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
        var notTaken = state.PreferencesNotApplied.Intersect(DrawingPreferences).ToList();
        Assert.True(notTaken.Count == 0, "drawing preferences SOLIDWORKS did not take: " + string.Join(", ", notTaken));
        return $"SOLIDWORKS {status.SwRevision}, add-in {status.AddinVersion}, protocol {status.ProtocolVersion}; the nine drawing preferences took";
    });

    [SkippableFact, Step(2)]
    public Task The_block_with_its_hole_is_built_and_saved() => _run.CaseAsync(2, "a 60 x 40 x 20 mm block with a Ø10 mm hole through it at (20, 20), saved and closed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var block = await Call<DiagScratchBlockRequest, DocumentInfo>(new DiagScratchBlockRequest { Path = New("block", ".SLDPRT"), XMm = 60, YMm = 40, ZMm = 20 });
        var sketch = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = block.Path, On = "front", Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = new Point2(20, 20), RadiusMm = 5 } },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = block.Path, Sketch = sketch.Sketch.Name, Mode = FeatureMode.Cut, End = ExtrudeEnd.ThroughAll });
        var box = await Call<ModelBoundingBoxRequest, ModelBoundingBoxResult>(new ModelBoundingBoxRequest { Document = block.Path, Precise = true });
        Assert.NotNull(box.Box);
        Near(60, box.Box!.Max.X, "block x");
        Near(40, box.Box.Max.Y, "block y");
        Near(20, box.Box.Max.Z, "block z");
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = block.Path });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = block.Path });
        return $"{Path.GetFileName(block.Path)}: box {box.Box.Min}..{box.Box.Max}, hole {sketch.Sketch.Name} cut through";
    });

    [SkippableFact, Step(3)]
    public Task A_new_drawing_has_its_sheet_no_view_and_no_command_open() => _run.CaseAsync(3, "document.new type drawing: A3 landscape 1:1 third angle read back, one sheet, no view, no Model View command left open", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var made = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest
        {
            Type = DocumentType.Drawing, Path = New("block", ".SLDDRW"),
            Sheet = new SheetSetup { Size = SheetSize.A3, Orientation = SheetOrientation.Landscape, Scale = new ScaleRatio(1, 1), Projection = Projection.ThirdAngle },
        });
        Assert.Equal(DocumentType.Drawing, made.Document.Type);
        Assert.True(File.Exists(Drawing));
        var sheet = made.Sheet!;
        Assert.Equal(SheetSize.A3, sheet.Size);
        Near(420, sheet.WidthMm, "sheet width");
        Near(297, sheet.HeightMm, "sheet height");
        Assert.Equal(Projection.ThirdAngle, sheet.Projection);
        var read = await Read();
        Assert.Single(read.Sheets);
        Assert.Empty(read.Sheets[0].Views);
        Assert.Null((await _run.AddinStateAsync()).Dialog);
        // The default template's sheet has its format held in the template ("*.drt"): read as that, not as none (5.A review).
        Assert.True(sheet.Format != null, "the default template's sheet read back with no format");
        Assert.Equal(sheet.Format, read.Sheets[0].Format);
        return $"made {Path.GetFileName(Drawing)} from {Path.GetFileName(made.Template)}: {sheet.Name} {sheet.WidthMm} x {sheet.HeightMm} mm, {sheet.Scale}, format {sheet.Format}; no command was left open";
    });

    [SkippableFact, Step(4)]
    public Task A_never_saved_model_is_refused_with_no_dialog() => _run.CaseAsync(4, "view.create of a never-saved part: invalid_argument before any SOLIDWORKS call, no Save As box", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var part = await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Part });
        try
        {
            var refused = await Refused(() => Call<ViewCreateRequest, ViewResult>(new ViewCreateRequest
            {
                Document = Drawing, Model = part.Document.Title, Orientation = "front", At = new SheetPoint(120, 180),
            }));
            Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
            Assert.Equal("/model", refused.Error.Details!["field"]);
            Assert.Contains("sw_document_save_as", refused.Message);
            Assert.Null((await _run.AddinStateAsync()).Dialog);
            Assert.Empty((await Read()).Sheets[0].Views);
            return $"refused: {refused.Message}";
        }
        finally
        {
            // A document never saved has no path, so the guard would call it the owner's: it goes before the next case.
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = part.Document.Title, Discard = true });
        }
    });

    [SkippableFact, Step(5)]
    public Task Model_and_projected_views_draw_the_block_where_the_transform_puts_it() => _run.CaseAsync(5, "front, top and isometric model views, right and above projected: each view's drawn extents the block's projected box to 0.01 mm", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        Task<ViewResult> Create(string orientation, double x, double y) => Call<ViewCreateRequest, ViewResult>(new ViewCreateRequest
        {
            Document = Drawing, Model = Block, Orientation = orientation, At = new SheetPoint(x, y),
        });
        var front = await Create("front", 120, 180);
        Box(front.View.Extents, 90, 150, 160, 200, "front");
        Assert.Equal(ViewDisplay.HiddenLinesVisible, front.View.Display);
        Assert.False(front.ModelUnsaved);
        Assert.False(front.Change.Models.Single().DirtyAfter);
        var top = await Create("top", 120, 80);
        Box(top.View.Extents, 90, 150, 70, 90, "top");
        var iso = await Create("isometric", 330, 200);
        Assert.Equal("isometric", iso.View.Orientation);
        Assert.Equal(ViewDisplay.HiddenLinesRemoved, iso.View.Display);
        var right = await Call<ViewProjectRequest, ViewResult>(new ViewProjectRequest { Document = Drawing, Parent = front.View.Reference!, Direction = ProjectDirection.Right });
        Box(right.View.Extents, 170, 190, 160, 200, "right (projected)");
        Near(180, right.View.Position.Y, "right on the front's horizontal centre line");
        var above = await Call<ViewProjectRequest, ViewResult>(new ViewProjectRequest { Document = Drawing, Parent = front.View.Name, Direction = ProjectDirection.Above });
        Box(above.View.Extents, 90, 150, 220, 240, "above (projected)");
        Near(120, above.View.Position.X, "above on the front's vertical centre line");
        Views["front"] = front.View.Name;
        Views["top"] = top.View.Name;
        Views["iso"] = iso.View.Name;
        Views["right"] = right.View.Name;
        Views["above"] = above.View.Name;
        var annotations = new[] { front, top, iso, right, above }.Select(v => $"{v.View.Name} {v.View.Annotations}").ToList();
        return $"front {front.View.Extents}, top {top.View.Extents}, iso {iso.View.Extents}, right {right.View.Extents} at {right.View.Position}, above {above.View.Extents} at {above.View.Position}; " +
               $"annotations inserted with them: {string.Join(", ", annotations)}";
    });

    [SkippableFact, Step(6)]
    public Task Sheets_are_added_changed_shown_and_deleted() => _run.CaseAsync(6, "sheet.add A4 portrait 2:1 first angle and a custom 500 x 300; sheet.set scale and projection; sheet.activate; sheet.delete; the last sheet refused", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var details = await Call<SheetAddRequest, SheetResult>(new SheetAddRequest
        {
            Document = Drawing, Name = "Details", Size = SheetSize.A4, Orientation = SheetOrientation.Portrait, Scale = new ScaleRatio(2, 1), Projection = Projection.FirstAngle,
        });
        Assert.Equal(SheetSize.A4, details.Sheet.Size);
        Near(210, details.Sheet.WidthMm, "A4 portrait width");
        Near(297, details.Sheet.HeightMm, "A4 portrait height");
        Assert.Equal(2, details.Sheet.Scale.Numerator);
        Assert.Equal(Projection.FirstAngle, details.Sheet.Projection);
        // No format asked: the first sheet's, the template's own one included (5.A review).
        string? firstFormat = (await Read()).Sheets.Single(s => s.Name == "Sheet1").Format;
        Assert.True(firstFormat != null, "Sheet1 read back with no format");
        Assert.Equal(firstFormat, details.Sheet.Format);
        var wide = await Call<SheetAddRequest, SheetResult>(new SheetAddRequest { Document = Drawing, Name = "Wide", WidthMm = 500, HeightMm = 300 });
        Assert.Null(wide.Sheet.Size);
        Near(500, wide.Sheet.WidthMm, "custom width");
        var set = await Call<SheetSetRequest, SheetResult>(new SheetSetRequest { Document = Drawing, Sheet = wide.Sheet.Reference!, Scale = new ScaleRatio(1, 5), Projection = Projection.FirstAngle });
        Assert.Equal(5, set.Sheet.Scale.Denominator);
        Assert.Equal(Projection.FirstAngle, set.Sheet.Projection);
        var shown = await Call<SheetActivateRequest, SheetActivateResult>(new SheetActivateRequest { Document = Drawing, Sheet = "Sheet1" });
        Assert.Equal("Wide", shown.Before);
        var deleted = await Call<SheetDeleteRequest, SheetDeleteResult>(new SheetDeleteRequest { Document = Drawing, Sheets = { "Wide" } });
        Assert.Equal(new[] { "Wide" }, deleted.Deleted);
        var last = await Refused(() => Call<SheetDeleteRequest, SheetDeleteResult>(new SheetDeleteRequest { Document = Drawing, Sheets = { "Sheet1", "Details" } }));
        Assert.Equal("/sheets", last.Error.Details!["field"]);
        var read = await Read();
        Assert.Equal(new[] { "Sheet1", "Details" }, read.Sheets.Select(s => s.Name));
        Assert.Equal("Sheet1", read.ActiveSheet);
        return $"Details {details.Sheet.WidthMm} x {details.Sheet.HeightMm} {details.Sheet.Scale} first angle, format {details.Sheet.Format} as Sheet1's; Wide 500 x 300 then {set.Sheet.Scale}; Sheet1 shown; Wide deleted; deleting the rest refused ({last.Message})";
    });

    [SkippableFact, Step(7)]
    public Task Views_move_rescale_restyle_and_delete_and_projected_views_stay_aligned() => _run.CaseAsync(7, "view.set move (projected views follow), off-alignment refused, rescale, display; sheet.set scale redraws views that follow it; view.delete refuses a parent; a deleted view's reference unresolved", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var moved = await Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = Views["front"], At = new SheetPoint(130, 180) });
        Box(moved.View.Extents, 100, 160, 160, 200, "front moved");
        Near(130, (await View(Views["above"])).Position.X, "above follows the front along x");
        Near(180, (await View(Views["right"])).Position.Y, "right stays on y 180");
        // SOLIDWORKS drags a projected view the whole way with its parent (5.A live run), not only along its centre line.
        Near(190, (await View(Views["right"])).Position.X, "right moves 10 mm along x with the front");
        var offLine = await Refused(() => Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = Views["right"], At = new SheetPoint(200, 170) }));
        Assert.Equal("/at/y", offLine.Error.Details!["field"]);
        var iso = await Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = Views["iso"], Scale = new ScaleRatio(1, 2), Display = ViewDisplay.Shaded });
        Assert.Equal(ViewDisplay.Shaded, iso.View.Display);
        Assert.False(iso.View.SheetScale);

        var half = await Call<SheetSetRequest, SheetResult>(new SheetSetRequest { Document = Drawing, Sheet = "Sheet1", Scale = new ScaleRatio(1, 2) });
        var frontHalf = half.Sheet.Views.Single(v => v.Name == Views["front"]);
        Near(30, frontHalf.Extents!.Max.X - frontHalf.Extents.Min.X, "the front at the sheet's 1:2, width");
        Near(20, frontHalf.Extents.Max.Y - frontHalf.Extents.Min.Y, "the front at the sheet's 1:2, height");
        await Call<SheetSetRequest, SheetResult>(new SheetSetRequest { Document = Drawing, Sheet = "Sheet1", Scale = new ScaleRatio(1, 1) });

        var parent = await Refused(() => Call<ViewDeleteRequest, ViewDeleteResult>(new ViewDeleteRequest { Document = Drawing, Views = { Views["front"] } }));
        Assert.Equal("/views/0", parent.Error.Details!["field"]);
        string aboveRef = (await View(Views["above"])).Reference!;
        var gone = await Call<ViewDeleteRequest, ViewDeleteResult>(new ViewDeleteRequest { Document = Drawing, Views = { aboveRef } });
        Assert.Equal(new[] { Views["above"] }, gone.Deleted);
        var stale = await Refused(() => Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = aboveRef, Display = ViewDisplay.Wireframe }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
        Assert.Equal("not_found", stale.Error.Details!["reason"]);
        return $"front moved to {moved.View.Position} ({moved.View.Extents}), above and right followed; off-line move refused; iso 1:2 shaded; at the sheet's 1:2 the front drew {frontHalf.Extents}; " +
               $"deleting the front alone refused ({parent.Message}); above deleted, its reference {WireNames.ForEnum(stale.Code)} ({stale.Error.Details["reason"]})";
    });

    [SkippableFact, Step(8)]
    public Task The_front_view_shows_the_hole_where_it_is() => _run.CaseAsync(8, "view.entities of the front view: the hole's circle centred at (120, 180), radius 5; model references through a view", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var shown = await Call<ViewEntitiesRequest, ViewEntitiesResult>(new ViewEntitiesRequest { Document = Drawing, View = Views["front"] });
        Assert.Equal(1, shown.Scale);
        // The front is centred at (130, 180), so the block's origin is drawn at (100, 160) and the hole at (120, 180).
        var holes = shown.Entities.Where(e => e.Shape == DrawnShape.Circle && Math.Abs(e.RadiusMm!.Value - 5) <= Tol).ToList();
        var hole = Assert.Single(holes);
        Near(120, hole.Centre!.X, "hole centre x");
        Near(180, hole.Centre.Y, "hole centre y");
        int referenced = shown.Entities.Count(e => e.Reference != null);
        string resolves = "no reference to check";
        if (hole.Reference != null)
        {
            var found = await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = Block, Kind = TopologyKind.Edge, Geometry = GeometryType.Circle });
            resolves = found.Entities.Any(e => e.Reference == hole.Reference) ? "the hole's reference is one model.find issues in the part" : "the hole's reference differs from model.find's, but was issued in the part";
        }
        return $"{shown.Entities.Count} entities ({shown.Entities.Count(e => e.Kind == ReferenceKind.Edge)} edges, {shown.Entities.Count(e => e.Silhouette)} silhouettes, {shown.Entities.Count(e => e.Kind == ReferenceKind.Vertex)} vertices); " +
               $"{referenced} with a model reference; hole at {hole.Centre} r {hole.RadiusMm}; {resolves}";
    });

    [SkippableFact, Step(9)]
    public Task SOLIDWORKS_persistent_ids_for_a_sheet_and_a_view_are_probed() => _run.CaseAsync(9, "information only: whether GetPersistReference3 gives a sheet and a view an id (api.call reports an object's reference when it can persist it)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = Drawing, Target = "document", Member = "ActivateView", Args = new List<object?> { Views["front"] }, Rebuild = false });
        var view = await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = Drawing, Target = "document", Member = "ActiveDrawingView", Rebuild = false });
        var sheet = await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = Drawing, Target = "document", Member = "GetCurrentSheet", Rebuild = false });
        return $"view: {JsonCodec.Serialize(view.Result)}; sheet: {JsonCodec.Serialize(sheet.Result)} (a ref means GetPersistReference3 gave an id)";
    });

    [SkippableFact, Step(10)]
    public Task Drawing_read_agrees_with_everything_made() => _run.CaseAsync(10, "drawing.read: Sheet1 with front, top, right and iso where the cases left them, Details empty, the block the one model", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var read = await Read();
        var sheet1 = read.Sheets.Single(s => s.Name == "Sheet1");
        Assert.Equal(new[] { Views["front"], Views["top"], Views["iso"], Views["right"] }.OrderBy(n => n), sheet1.Views.Select(v => v.Name).OrderBy(n => n));
        Assert.Empty(read.Sheets.Single(s => s.Name == "Details").Views);
        var byName = sheet1.Views.ToDictionary(v => v.Name);
        Box(byName[Views["front"]].Extents, 100, 160, 160, 200, "front");
        Box(byName[Views["top"]].Extents, 90, 150, 70, 90, "top");
        Box(byName[Views["right"]].Extents, 180, 200, 160, 200, "right");
        Assert.Equal(ViewType.Projected, byName[Views["right"]].Type);
        Assert.Equal(Views["front"], byName[Views["right"]].Parent);
        var model = Assert.Single(read.Models);
        Assert.True(string.Equals(Block, model.Path, StringComparison.OrdinalIgnoreCase), model.Path);
        Assert.False(model.Missing);
        return $"{read.Sheets.Count} sheets, {sheet1.Views.Count} views on Sheet1 as the cases left them; model {Path.GetFileName(model.Path)} loaded {model.Loaded}, dirty {model.Dirty}";
    });

    /// <summary>Changes the block's depth and back, so it has unsaved changes and the same shape.</summary>
    async Task Dirty()
    {
        var dims = await Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = Block });
        var depth = dims.Dimensions.Single(d => d.Feature != null && d.Feature.StartsWith("Boss-Extrude", StringComparison.Ordinal) && Math.Abs(d.Value - 20) < 1e-9);
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = Block, Dimension = depth.Name, Value = 21 });
        await Call<DimensionSetRequest, DimensionSetResult>(new DimensionSetRequest { Document = Block, Dimension = depth.Name, Value = 20 });
    }

    [SkippableFact, Step(11)]
    public Task Saving_the_drawing_saves_its_dirty_model_first() => _run.CaseAsync(11, "document.save of the drawing writes the block it shows (unsaved changes) and then the drawing, both on disk", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = Block });
        await Dirty();
        Assert.True((await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, Block, StringComparison.OrdinalIgnoreCase)).Dirty);
        var blockBefore = File.GetLastWriteTimeUtc(Block);
        var drawingBefore = File.GetLastWriteTimeUtc(Drawing);
        var saved = await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = Drawing });
        Assert.Contains(saved.Referenced, r => string.Equals(r.Path, Block, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.GetLastWriteTimeUtc(Block) > blockBefore, "the block's file was not written");
        Assert.True(File.GetLastWriteTimeUtc(Drawing) > drawingBefore, "the drawing's file was not written");
        Assert.DoesNotContain((await _run.AddinStateAsync()).Documents, d => d.Dirty);
        return $"saved {string.Join(", ", saved.Referenced.Select(r => $"{Path.GetFileName(r.Path)} ({r.Bytes} bytes)"))}, then {Path.GetFileName(saved.Path)} ({saved.Bytes} bytes)";
    });

    [SkippableFact, Step(12)]
    public Task Closing_the_drawing_protects_its_dirty_model() => _run.CaseAsync(12, "the block dirty and without its window: closing the drawing refused naming it; saved, then closed, unloading it", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Dirty();
        var window = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = Block });
        Assert.Contains(Drawing, window.StillUsedBy, StringComparer.OrdinalIgnoreCase);
        var hidden = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, Block, StringComparison.OrdinalIgnoreCase));
        Assert.False(hidden.Visible);
        Assert.True(hidden.Dirty);
        var refused = await Refused(() => Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = Drawing }));
        Assert.Equal("discard", refused.Error.Details!["field"]);
        Assert.Contains(Block, ((List<object?>)refused.Error.Details["unsaved"]!).Cast<string>(), StringComparer.OrdinalIgnoreCase);
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = Drawing });
        var closed = await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = Drawing });
        Assert.Contains(closed.Unloaded, u => string.Equals(u.Path, Block, StringComparison.OrdinalIgnoreCase));
        Assert.Empty((await _run.AddinStateAsync()).Documents);
        return $"the block lost its window, still used by the drawing, dirty; closing the drawing refused ({refused.Message}); saved and closed, unloading {string.Join(", ", closed.Unloaded.Select(u => u.Title))}";
    });

    [SkippableFact, Step(13)]
    public Task A_reopened_drawing_is_active_and_its_references_still_name_its_views() => _run.CaseAsync(13, "document.open of the drawing: nothing missing, the drawing active (not its model), a view reference issued before the close still resolves", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var opened = await Call<DocumentOpenRequest, DocumentOpenResult>(new DocumentOpenRequest { Path = Drawing });
        Assert.Empty(opened.Missing);
        Assert.Empty(opened.Substituted);
        var status = await Sw.CallAsync<SessionStatusRequest, SessionStatusResult>(new SessionStatusRequest(), TimeSpan.FromSeconds(60));
        Assert.True(string.Equals(status.ActiveDocument?.Path, Drawing, StringComparison.OrdinalIgnoreCase), $"active: {status.ActiveDocument?.Title}");
        var read = await Read();
        var front = read.Sheets.SelectMany(s => s.Views).Single(v => v.Name == Views["front"]);
        Box(front.Extents, 100, 160, 160, 200, "front after the reopen");
        var hidden = (await _run.AddinStateAsync()).Documents.Single(d => string.Equals(d.Path, Block, StringComparison.OrdinalIgnoreCase));
        return $"opened with warnings {string.Join(", ", opened.Warnings.DefaultIfEmpty("none"))}; active {status.ActiveDocument?.Title}; the block loaded with a window: {hidden.Visible}; front still at {front.Position}";
    });

    /// <summary>A sheet format file SOLIDWORKS installs (its sheetformat folder), for a format change to roll back.</summary>
    static string? InstalledFormat()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SOLIDWORKS");
        if (!Directory.Exists(root)) return null;
        var all = Directory.EnumerateFiles(root, "*.slddrt", SearchOption.AllDirectories).ToList();
        return all.FirstOrDefault(p => string.Equals(Path.GetFileName(p), "a3 - iso.slddrt", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }

    /// <summary>ISheet values of the sheet shown, through api.call (in the add-in): GetProperties2 and GetTemplateName.</summary>
    async Task<string> ShownSheet(string drawing)
    {
        var props = await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = drawing, Target = "document", Member = "GetCurrentSheet.GetProperties2", Rebuild = false });
        var name = await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = drawing, Target = "document", Member = "GetCurrentSheet.GetTemplateName", Rebuild = false });
        return $"GetProperties2 {JsonCodec.Serialize(props.Result)}, GetTemplateName {JsonCodec.Serialize(name.Result)}";
    }

    [SkippableFact, Step(14)]
    public Task A_template_s_own_format_is_kept_by_sheet_add_and_never_reported_put_back_when_it_was_not() => _run.CaseAsync(14, "on a new drawing from the default template: sheet.add with no format gets Sheet1's own format (*.drt); sheet.set to a format file, cancelled mid-change, either reads the old format back or says rollback_failed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string formats = New("formats", ".SLDDRW");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Type = DocumentType.Drawing, Path = formats });
        var notes = new List<string>();
        var failures = new List<string>();
        try
        {
            async Task<SheetInfo> SheetOf(string name) =>
                (await Call<DrawingReadRequest, DrawingReadResult>(new DrawingReadRequest { Document = formats })).Sheets.Single(s => s.Name == name);
            string? own = (await SheetOf("Sheet1")).Format;
            notes.Add($"Sheet1 format {own ?? "none"} ({await ShownSheet(formats)})");
            Assert.True(own != null, "the default template's Sheet1 read back with no format");

            try
            {
                var added = await Call<SheetAddRequest, SheetResult>(new SheetAddRequest { Document = formats, Name = "Second" });
                notes.Add($"sheet.add Second: format {added.Sheet.Format ?? "none"} ({await ShownSheet(formats)})");
                if (!string.Equals(own, added.Sheet.Format, StringComparison.OrdinalIgnoreCase)) failures.Add($"sheet.add gave Second the format {added.Sheet.Format ?? "none"}, not Sheet1's {own}");
            }
            catch (SwAgentException e)
            {
                failures.Add($"sheet.add with no format failed: {WireNames.ForEnum(e.Code)}: {e.Message}");
            }

            string file = InstalledFormat() ?? throw _run.Stop($"no .slddrt under {Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\\SOLIDWORKS for the format change");
            await Call<SheetActivateRequest, SheetActivateResult>(new SheetActivateRequest { Document = formats, Sheet = "Sheet1" });
            const string Title = "swmcp live 5.A format rollback";
            await Call<DiagArmDialogRequest, DiagArmDialogResult>(new DiagArmDialogRequest { Title = Title, Text = "Drawings suite: a dialog during a sheet format change", Buttons = DiagDialogButtons.OkCancel });
            var open = await Refused(() => Call<SheetSetRequest, SheetResult>(new SheetSetRequest { Document = formats, Sheet = "Sheet1", Format = file }));
            Assert.Equal(ErrorCode.DialogOpen, open.Code);
            var dialog = (await _run.AddinStateAsync()).Dialog!;
            Assert.Equal(Title, dialog.Title);
            var cancel = await Call<DialogRespondRequest, DialogRespondResult>(new DialogRespondRequest { Dialog = dialog.Id, Button = "Cancel", WaitMs = 30000 });
            Assert.Equal(DialogOutcome.Error, cancel.Outcome);
            Assert.Equal(ErrorCode.OperationFailed, cancel.Error!.Code);
            bool rollbackFailed = cancel.Error.Details?["rollback_failed"] is true;
            string? after = (await SheetOf("Sheet1")).Format;
            bool restored = string.Equals(after, own, StringComparison.OrdinalIgnoreCase);
            notes.Add($"sheet.set {Path.GetFileName(file)} cancelled: rollback_failed {rollbackFailed}, Sheet1 format after {after ?? "none"} ({await ShownSheet(formats)}); {cancel.Error.Message}");
            // Never a clean rollback that is not one: the format read back is the old one, or the answer says it is not.
            if (!restored && !rollbackFailed) failures.Add($"the rollback was reported clean, but Sheet1 reads {after ?? "none"}, not {own}");
            if (restored && rollbackFailed) failures.Add($"rollback_failed reported, but Sheet1 reads its old format {own}");
        }
        finally
        {
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = formats, Discard = true });
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures) + " | " + string.Join("; ", notes));
        return string.Join("; ", notes);
    });

    /// <summary>The hole's circular edge on the block's front face (z 20), as model.find issues it: centred on the hole's axis at (20, 20, 20).</summary>
    async Task<string> HoleEdge()
    {
        var found = await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = Block, Kind = TopologyKind.Edge, Geometry = GeometryType.Circle, Near = new Point3(25, 20, 20), MaxDistanceMm = 0.001 });
        return Assert.Single(found.Entities).Reference!;
    }

    [SkippableFact, Step(15)]
    public Task A_section_through_the_holes_axis_hatches_the_block_either_side_of_the_hole() => _run.CaseAsync(15, "view.section of the front view, a horizontal line through the hole's axis: section A below the front on its centre line, 60 x 20 drawn, two hatched faces of (60 - 10) x 20 = 1000 mm² in the plane y 20; a line beside the block refused", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string hole = await HoleEdge();
        var beside = await Refused(() => Call<ViewSectionRequest, ViewSectionResult>(new ViewSectionRequest
        {
            Document = Drawing, Parent = Views["front"], Cut = new SectionCut { Through = new ModelPlace { Point = new Point3(20, 45, 10) }, Orientation = CutOrientation.Horizontal },
        }));
        Assert.Equal("/cut/through", beside.Error.Details!["field"]);
        var clock = Stopwatch.StartNew();
        var section = await Call<ViewSectionRequest, ViewSectionResult>(new ViewSectionRequest
        {
            Document = Drawing, Parent = Views["front"], Cut = new SectionCut { Through = new ModelPlace { Reference = hole }, Orientation = CutOrientation.Horizontal },
        });
        long ms = clock.ElapsedMilliseconds;
        Assert.Equal("A", section.Label);
        Assert.Equal(ViewType.Section, section.View.Type);
        Assert.Equal(Views["front"], section.View.Parent);
        Near(20, section.Through.Y, "the cut's y, the hole's axis");
        Assert.Equal(2, section.HatchedFaces);
        Near(1000, section.HatchedAreaMm2, "the hatched area, (60 - 10) x 20 mm²: the hole's two edges 10 mm apart");
        var e = section.View.Extents!;
        Near(100, e.Min.X, "section min x, the front's");
        Near(160, e.Max.X, "section max x, the front's");
        Near(20, e.Max.Y - e.Min.Y, "section height, the block's 20 mm depth");
        Near(130, section.View.Position.X, "on the front's vertical centre line");
        Assert.False(section.Change.Models.Single().DirtyAfter);
        Views["section"] = section.View.Name;
        return $"refused beside the block ({beside.Message}); {section.View.Name} ({section.Label}-{section.Label}, reversed {section.Reversed}) through {section.ThroughIs} at {section.View.Position}, drew {section.View.Extents}; " +
               FormattableString.Invariant($"{section.HatchedFaces} hatched faces, {section.HatchedAreaMm2:0.###} mm²; {ms} ms");
    });

    [SkippableFact, Step(16)]
    public Task A_detail_around_the_hole_shows_it_at_2_to_1_or_is_refused_as_the_known_gap() => _run.CaseAsync(16, "view.detail of the front view around the hole, r 8 at 2:1 at (330, 90): detail B whose drawn edges lie in its circle and show the hole as a circle of r 10 at its centre, reported (and read by drawing.read) at (330, 90); or, if SP4 makes it empty, refused with check empty and nothing left (the stated gap)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string hole = await HoleEdge();
        int before = (await Read()).Sheets.Sum(s => s.Views.Count);
        ViewDetailResult detail;
        try
        {
            detail = await Call<ViewDetailRequest, ViewDetailResult>(new ViewDetailRequest
            {
                Document = Drawing, Parent = Views["front"], Centre = new ModelPlace { Reference = hole }, RadiusMm = 8, Scale = new ScaleRatio(2, 1), At = new SheetPoint(330, 90),
            });
        }
        catch (SwAgentException e) when (e.Code == ErrorCode.VerificationFailed && e.Error.Details?["check"] as string == "empty")
        {
            Assert.Equal(before, (await Read()).Sheets.Sum(s => s.Views.Count));
            return $"KNOWN GAP: SOLIDWORKS made an empty detail through the add-in; refused and rolled back, nothing left ({e.Message})";
        }
        Assert.Equal("B", detail.Label);
        Assert.Equal(2, detail.View.Scale.Numerator);
        // Where it is: the hole's centre drawn at (330, 90) (below), which is also its reported position (5.B review: not
        // SOLIDWORKS' Position, its own origin).
        Near(330, detail.View.Position.X, "the detail's position, x");
        Near(90, detail.View.Position.Y, "the detail's position, y");
        var read = await View(detail.View.Name);
        Near(330, read.Position.X, "drawing.read's position of the detail, x");
        Near(90, read.Position.Y, "drawing.read's position of the detail, y");
        Assert.True(detail.PointsInside > 0, "the detail draws edges");
        var shown = await Call<ViewEntitiesRequest, ViewEntitiesResult>(new ViewEntitiesRequest { Document = Drawing, View = detail.View.Name });
        var circle = Assert.Single(shown.Entities, x => x.Shape == DrawnShape.Circle && Math.Abs(x.RadiusMm!.Value - 10) <= Tol);
        Near(330, circle.Centre!.X, "the hole's centre in the detail, x");
        Near(90, circle.Centre.Y, "the hole's centre in the detail, y");
        Views["detail"] = detail.View.Name;
        return $"{detail.View.Name} ({detail.Label}) around {detail.CentreIs} at {detail.View.Position} (drawing.read {read.Position}), {detail.PointsInside} drawn points inside its circle, extents {detail.View.Extents}; the hole drawn r {circle.RadiusMm} at {circle.Centre}";
    });

    /// <summary>The tab's straight edge on its base face nearest (x, y), as model.find gives it within 1 µm at the face's level.</summary>
    async Task<string> TabEdge(string path, double x, double y)
    {
        var face = (await Call<SheetMetalReadRequest, SheetMetalReadResult>(new SheetMetalReadRequest { Document = path })).BaseFace!;
        var n = face.Normal!;
        double level = n.X * face.Origin!.X + n.Y * face.Origin.Y + n.Z * face.Origin.Z;
        var found = await Call<ModelFindRequest, ModelFindResult>(new ModelFindRequest { Document = path, Kind = TopologyKind.Edge, Near = new Point3(x, y, level * n.Z), MaxDistanceMm = 0.001 });
        return Assert.Single(found.Entities).Reference!;
    }

    string Tab => Path.Combine(_run.Guard.ScratchDirectory, "tab.SLDPRT");

    [SkippableFact, Step(17)]
    public Task The_tabs_flat_pattern_is_drawn_at_its_flat_size_with_one_bend_line() => _run.CaseAsync(17, "view.flat_pattern of phase 6's tab (100 x 60, 2 mm, R 3, a 25 mm flange, saved and closed) on Details at 1:1 at (105, 150): opened for it, 100 x 83.2832 drawn, one bend line, bend notes shown, still folded; the block refused as not sheet metal", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        string path = New("tab", ".SLDPRT");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = path });
        var outline = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = path, On = "front", Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "sheet", Corner1 = new Point2(0, 0), Corner2 = new Point2(100, 60) } },
        });
        await Call<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>(new SheetMetalBaseFlangeRequest { Document = path, Sketch = outline.Sketch.Name, ThicknessMm = 2, BendRadiusMm = 3 });
        await Call<SheetMetalEdgeFlangeRequest, SheetMetalFeatureResult>(new SheetMetalEdgeFlangeRequest
        {
            Document = path, Edges = new List<string> { await TabEdge(path, 50, 60) }, LengthMm = 25, LengthFrom = FlangeLengthFrom.InnerVirtualSharp,
        });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = path });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = path });

        var notSheet = await Refused(() => Call<ViewFlatPatternRequest, ViewFlatPatternResult>(new ViewFlatPatternRequest { Document = Drawing, Sheet = "Details", Model = Block, At = new SheetPoint(105, 150) }));
        Assert.Equal(ErrorCode.WrongDocumentType, notSheet.Code);
        Assert.Equal("sheet_metal", notSheet.Error.Details!["needs"]);
        var clock = Stopwatch.StartNew();
        var flat = await Call<ViewFlatPatternRequest, ViewFlatPatternResult>(new ViewFlatPatternRequest
        {
            Document = Drawing, Sheet = "Details", Model = path, At = new SheetPoint(105, 150), Scale = new ScaleRatio(1, 1),
        });
        long ms = clock.ElapsedMilliseconds;
        Assert.Equal(ViewType.FlatPattern, flat.View.Type);
        Assert.True(flat.Opened, "the tab was closed, so the view opened it");
        var e = flat.View.Extents!;
        double w = e.Max.X - e.Min.X, h = e.Max.Y - e.Min.Y;
        Assert.True(Math.Abs(Math.Max(w, h) - 100) <= 0.01 + 0.005, $"the flat pattern's long side: {Math.Max(w, h)}");
        Assert.True(Math.Abs(Math.Min(w, h) - 83.2832) <= 0.01 + 0.005, $"the flat pattern's short side: {Math.Min(w, h)}");
        Assert.Equal(1, flat.BendLines);
        Assert.True(flat.BendNotes);
        Assert.Equal(SheetState.Folded, flat.State);
        Views["flat"] = flat.View.Name;
        return FormattableString.Invariant($"refused the block ({notSheet.Message}); {flat.View.Name} on Details: drew {w:0.####} x {h:0.####} ({flat.View.Extents}), flat {flat.Flat.LengthMm:0.####} x {flat.Flat.WidthMm:0.####}, ") +
               $"{flat.BendLines} bend line, bend notes {flat.BendNotes}, {WireNames.ForEnum(flat.State)}, opened {flat.Opened}; configuration {flat.View.Configuration}; added {string.Join(", ", flat.ConfigurationsAdded.DefaultIfEmpty("none"))}; marked_dirty {flat.MarkedDirty}; tab dirty {flat.ModelUnsaved}; {ms} ms";
    });

    [SkippableFact, Step(18)]
    public Task A_section_moves_along_its_centre_line_and_a_detail_is_deleted_with_its_circle() => _run.CaseAsync(18, "view.set moves section A 10 mm down its centre line, drawing what it drew; off the line refused; view.set moves detail B 40 mm down, by its circle's centre, to (330, 50), the hole drawn there; view.delete of detail B (and its circle); the front refused while its section remains", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var was = await View(Views["section"]);
        var moved = await Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = Views["section"], At = new SheetPoint(was.Position.X, was.Position.Y - 10) });
        Near(was.Extents!.Min.Y - 10, moved.View.Extents!.Min.Y, "the section moved 10 mm down");
        Near(was.Extents.Min.X, moved.View.Extents.Min.X, "the section kept its x");
        var off = await Refused(() => Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = Views["section"], At = new SheetPoint(was.Position.X + 10, was.Position.Y) }));
        Assert.Equal("/at/x", off.Error.Details!["field"]);
        string detail = "no detail (case 16 recorded the gap)";
        if (Views.TryGetValue("detail", out var d))
        {
            var drew = await View(d);
            var shifted = await Call<ViewSetRequest, ViewResult>(new ViewSetRequest { Document = Drawing, View = d, At = new SheetPoint(330, 50) });
            Near(330, shifted.View.Position.X, "the detail's position after the move, x");
            Near(50, shifted.View.Position.Y, "the detail's position after the move, y");
            Near(drew.Extents!.Min.Y - 40, shifted.View.Extents!.Min.Y, "the detail moved 40 mm down");
            Near(drew.Extents.Min.X, shifted.View.Extents.Min.X, "the detail kept its x");
            var shown = await Call<ViewEntitiesRequest, ViewEntitiesResult>(new ViewEntitiesRequest { Document = Drawing, View = d });
            var hole = Assert.Single(shown.Entities, x => x.Shape == DrawnShape.Circle && Math.Abs(x.RadiusMm!.Value - 10) <= Tol);
            Near(330, hole.Centre!.X, "the hole's centre in the moved detail, x");
            Near(50, hole.Centre.Y, "the hole's centre in the moved detail, y");
            var gone = await Call<ViewDeleteRequest, ViewDeleteResult>(new ViewDeleteRequest { Document = Drawing, Views = { d } });
            Assert.Equal(new[] { d }, gone.Deleted);
            detail = $"{d} moved to {shifted.View.Position} ({shifted.View.Extents}), the hole drawn at {hole.Centre}; {d} deleted with its circle";
        }
        var parent = await Refused(() => Call<ViewDeleteRequest, ViewDeleteResult>(new ViewDeleteRequest { Document = Drawing, Views = { Views["front"] } }));
        Assert.Contains(Views["section"], ((List<object?>)parent.Error.Details!["dependents"]!).Cast<string>());
        return $"section moved to {moved.View.Position} ({moved.View.Extents}); off its line refused ({off.Message}); {detail}; the front refused ({parent.Message})";
    });

    [SkippableFact, Step(19)]
    public Task Drawing_read_shows_the_section_and_the_flat_pattern() => _run.CaseAsync(19, "drawing.read: Sheet1 holds section A of the front, Details the tab's flat pattern; the block and the tab its models, neither changed by the drawing", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var read = await Read();
        var section = read.Sheets.Single(s => s.Name == "Sheet1").Views.Single(v => v.Name == Views["section"]);
        Assert.Equal(ViewType.Section, section.Type);
        Assert.Equal(Views["front"], section.Parent);
        var flat = read.Sheets.Single(s => s.Name == "Details").Views.Single(v => v.Name == Views["flat"]);
        Assert.Equal(ViewType.FlatPattern, flat.Type);
        Assert.Contains(read.Models, m => string.Equals(m.Path, Tab, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(read.Models, m => string.Equals(m.Path, Block, StringComparison.OrdinalIgnoreCase));
        return $"Sheet1: {string.Join(", ", read.Sheets.Single(s => s.Name == "Sheet1").Views.Select(v => $"{v.Name} ({WireNames.ForEnum(v.Type)})"))}; Details: {flat.Name} ({WireNames.ForEnum(flat.Type)}, {flat.Configuration}); models {string.Join(", ", read.Models.Select(m => $"{Path.GetFileName(m.Path)} dirty {m.Dirty}"))}";
    });

    /// <summary>The annotations cases 21 to 26 make, by role: their references.</summary>
    static readonly Dictionary<string, string> Made = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The front view's left, right and bottom edges (its drawn extremes) and the hole's circle, as view.entities gives them, each with a reference.</summary>
    async Task<(ViewEntityInfo Left, ViewEntityInfo Right, ViewEntityInfo Bottom, ViewEntityInfo Hole)> FrontEdges()
    {
        var shown = (await Call<ViewEntitiesRequest, ViewEntitiesResult>(new ViewEntitiesRequest { Document = Drawing, View = Views["front"], Kinds = new List<ViewEntityKind> { ViewEntityKind.Edge } })).Entities;
        var lines = shown.Where(e => e.Shape == DrawnShape.Line && e.Reference != null).ToList();
        var vertical = lines.Where(e => Math.Abs(e.Start!.X - e.End!.X) <= Tol).OrderBy(e => e.Start!.X).ToList();
        var horizontal = lines.Where(e => Math.Abs(e.Start!.Y - e.End!.Y) <= Tol).OrderBy(e => e.Start!.Y).ToList();
        Assert.True(vertical.Count >= 2 && horizontal.Count >= 1, $"the front shows {vertical.Count} vertical and {horizontal.Count} horizontal edges with references");
        var hole = Assert.Single(shown, e => e.Shape == DrawnShape.Circle && e.Reference != null && Math.Abs(e.RadiusMm!.Value - 5) <= Tol);
        return (vertical.First(), vertical.Last(), horizontal.First(), hole);
    }

    ViewEntity InFront(ViewEntityInfo e) => new ViewEntity { View = Views["front"], Reference = e.Reference };

    Task<ModelDimensionsResult> BlockDimensions() => Call<ModelDimensionsRequest, ModelDimensionsResult>(new ModelDimensionsRequest { Document = Block });

    /// <summary>The block as the drawing's last change left it: not marked as changed by it.</summary>
    void BlockUnchanged(DrawingChange change)
    {
        var block = change.Models.Single(m => string.Equals(m.Path, Block, StringComparison.OrdinalIgnoreCase));
        Assert.True(block.DirtyBefore == block.DirtyAfter && block.RebuildErrorsBefore == block.RebuildErrorsAfter, "the block changed");
    }

    [SkippableFact, Step(20)]
    public Task The_title_block_properties_are_written_and_read_back_resolved() => _run.CaseAsync(20, "drawing.properties: Title, PartNo and a Description linking PartNo written, each read back as written (Get2) and resolved; read again without writing", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var r = await Call<DrawingPropertiesRequest, DrawingPropertiesResult>(new DrawingPropertiesRequest
        {
            Document = Drawing,
            Set = new List<DrawingPropertyValue>
            {
                new DrawingPropertyValue { Name = "Title", Value = "SWMCP BLOCK" },
                new DrawingPropertyValue { Name = "PartNo", Value = "BLK-060" },
                new DrawingPropertyValue { Name = "Description", Value = "$PRP:\"PartNo\" test block" },
            },
        });
        Assert.Equal(new[] { "Title", "PartNo", "Description" }, r.Written);
        Assert.Equal("BLK-060 test block", r.Properties.Single(p => p.Name == "Description").Resolved);
        var read = await Call<DrawingPropertiesRequest, DrawingPropertiesResult>(new DrawingPropertiesRequest { Document = Drawing });
        Assert.Empty(read.Written);
        Assert.Equal("SWMCP BLOCK", read.Properties.Single(p => p.Name == "Title").Value);
        return $"wrote {string.Join(", ", r.Written)}; read {string.Join("; ", read.Properties.Select(p => $"{p.Name} = '{p.Value}' -> '{p.Resolved}'"))}";
    });

    [SkippableFact, Step(21)]
    public Task Notes_show_linked_properties_and_point_at_the_hole() => _run.CaseAsync(21, "annotation.note: TITLE: $PRP:\"Title\" on Sheet1 at (300, 30) shown resolved; THRU ALL in the front view at (150, 215) with a leader to the hole's edge (by reference); annotation.set moves the sheet note to (310, 30) and gives it new text", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var title = await Call<AnnotationNoteRequest, AnnotationNoteResult>(new AnnotationNoteRequest { Document = Drawing, Sheet = "Sheet1", Text = "TITLE: $PRP:\"Title\"", At = new SheetPoint(300, 30) });
        Assert.Equal("TITLE: SWMCP BLOCK", title.Shown);
        Assert.Null(title.Annotation.View);
        var (_, _, _, hole) = await FrontEdges();
        var thru = await Call<AnnotationNoteRequest, AnnotationNoteResult>(new AnnotationNoteRequest { Document = Drawing, Text = "THRU ALL", At = new SheetPoint(150, 215), AttachTo = InFront(hole) });
        Assert.Equal(Views["front"], thru.Annotation.View);
        Assert.Single(thru.Annotation.Attached);
        BlockUnchanged(thru.Change);
        var moved = await Call<AnnotationSetRequest, AnnotationResult>(new AnnotationSetRequest { Document = Drawing, Annotation = title.Annotation.Reference!, At = new SheetPoint(310, 30), Text = "DRAWING: $PRP:\"Title\"" });
        Near(310, moved.Annotation.Position!.X, "the title note's x after the move");
        Assert.Equal("DRAWING: SWMCP BLOCK", moved.Annotation.Text);
        Made["title"] = moved.Annotation.Reference!;
        Made["thru"] = thru.Annotation.Reference!;
        return $"{title.Annotation.Name} on Sheet1 shows '{title.Shown}' at {title.Annotation.Position}; {thru.Annotation.Name} in {thru.Annotation.View} at {thru.Annotation.Position}, attached to {thru.Annotation.Attached[0].Reference?.Length ?? 0}-character reference; moved to {moved.Annotation.Position}, now '{moved.Annotation.Text}'";
    });

    [SkippableFact, Step(22)]
    public Task The_blocks_own_dimensions_come_in_equal_to_the_models() => _run.CaseAsync(22, "annotation.import of the block's dimensions into the front, top and right views: each one of model.dimensions' with its value to 0.001 mm; what SOLIDWORKS put into other views removed; the block unchanged", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var model = await BlockDimensions();
        var clock = Stopwatch.StartNew();
        var r = await Call<AnnotationImportRequest, AnnotationImportResult>(new AnnotationImportRequest { Document = Drawing, Views = new List<string> { Views["front"], Views["top"], Views["right"] } });
        long ms = clock.ElapsedMilliseconds;
        Assert.NotEmpty(r.Dimensions);
        foreach (var d in r.Dimensions)
        {
            var m = model.Dimensions.SingleOrDefault(x => string.Equals(x.Name, d.Dimension, StringComparison.OrdinalIgnoreCase));
            Assert.True(m != null, $"{d.Dimension} is not one of the block's ({string.Join(", ", model.Dimensions.Select(x => x.Name))})");
            Assert.True(Math.Abs(m!.Value - d.Value) <= 0.001, $"{d.Dimension}: {d.Value} on the drawing, {m.Value} in the model");
        }
        Assert.All(r.Annotations.Where(a => a.Kind == AnnotationKind.Dimension), a => Assert.True(a.ModelDimension == true, a.Name));
        BlockUnchanged(r.Change);
        Made["imported"] = r.Annotations.First(a => a.Kind == AnnotationKind.Dimension).Reference!;
        return FormattableString.Invariant($"{r.Annotations.Count} brought ({string.Join(", ", r.Dimensions.Select(d => $"{d.Dimension} = {d.Value:0.###} in {d.View}"))}); removed from other views {r.Removed}; duplicates {(r.Duplicates.Count == 0 ? "none" : string.Join("; ", r.Duplicates))}; model {string.Join(", ", model.Dimensions.Select(x => $"{x.Name} = {x.Value:0.###}"))}; {ms} ms");
    });

    [SkippableFact, Step(23)]
    public Task Reference_dimensions_read_the_blocks_width_the_holes_diameter_and_the_corners_angle() => _run.CaseAsync(23, "annotation.dimension in the front view, by reference: horizontal between the sides 60.000, the hole's diameter 10.000, the angle at the bottom-left corner 90.00°; each driven, the drawing's own; the block unchanged", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (left, right, bottom, hole) = await FrontEdges();
        async Task<AnnotationDimensionResult> Dimension(DimensionKind kind, SheetPoint at, params ViewEntityInfo[] entities) =>
            await Call<AnnotationDimensionRequest, AnnotationDimensionResult>(new AnnotationDimensionRequest { Document = Drawing, Kind = kind, Entities = entities.Select(InFront).ToList(), At = at });
        var clock = Stopwatch.StartNew();
        var width = await Dimension(DimensionKind.Horizontal, new SheetPoint(130, 150), left, right);
        long ms = clock.ElapsedMilliseconds;
        var diameter = await Dimension(DimensionKind.Diameter, new SheetPoint(90, 205), hole);
        var angle = await Dimension(DimensionKind.Angle, new SheetPoint(92, 155), bottom, left);
        Assert.True(Math.Abs(width.Value - 60) <= 0.001, $"width {width.Value}");
        Assert.True(Math.Abs(diameter.Value - 10) <= 0.001, $"diameter {diameter.Value}");
        Assert.True(Math.Abs(angle.Value - 90) <= 0.01, $"angle {angle.Value}");
        Assert.All(new[] { width, diameter, angle }, d => Assert.False(d.Annotation.ModelDimension));
        BlockUnchanged(angle.Change);
        Made["width"] = width.Annotation.Reference!;
        return FormattableString.Invariant($"{width.Annotation.Name} {width.Value:0.######} ({width.Annotation.DimensionKind}), {diameter.Annotation.Name} {diameter.Value:0.######}, {angle.Annotation.Name} {angle.Value:0.####}°; names {width.Annotation.DimensionName}, {diameter.Annotation.DimensionName}, {angle.Annotation.DimensionName}; first {ms} ms");
    });

    string Pin => Path.Combine(_run.Guard.ScratchDirectory, "pin.SLDPRT");

    [SkippableFact, Step(24)]
    public Task The_hole_takes_a_centre_mark_and_a_pin_seen_from_the_side_a_centre_line() => _run.CaseAsync(24, "annotation.centre_marks on the front: one, at the hole's centre (120, 180); again: already marked; a Ø10 x 30 pin (saved, closed) viewed from the top on Details: annotation.centre_lines makes one, attached to its cylinder; annotation.set refuses to move the mark (it sits on its circle), which stays at (120, 180)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var marks = await Call<AnnotationCentreMarksRequest, AnnotationCentresResult>(new AnnotationCentreMarksRequest { Document = Drawing, View = Views["front"] });
        Assert.Equal(1, marks.Eligible);
        var mark = Assert.Single(marks.Made);
        Near(120, mark.Position!.X, "the centre mark's x");
        Near(180, mark.Position.Y, "the centre mark's y");
        var again = await Call<AnnotationCentreMarksRequest, AnnotationCentresResult>(new AnnotationCentreMarksRequest { Document = Drawing, View = Views["front"] });
        Assert.Equal((1, 1), (again.Eligible, again.Already));
        Assert.Empty(again.Made);

        string pin = New("pin", ".SLDPRT");
        await Call<DocumentNewRequest, DocumentNewResult>(new DocumentNewRequest { Path = pin });
        var circle = await Call<SketchCreateRequest, SketchCreateResult>(new SketchCreateRequest
        {
            Document = pin, On = "front", Circles = new List<SketchCircle> { new SketchCircle { Id = "pin", Center = new Point2(0, 0), RadiusMm = 5 } },
        });
        await Call<FeatureExtrudeRequest, FeatureResult>(new FeatureExtrudeRequest { Document = pin, Sketch = circle.Sketch.Name, DepthMm = 30 });
        await Call<DocumentSaveRequest, DocumentSaveResult>(new DocumentSaveRequest { Document = pin });
        await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = pin });
        var view = await Call<ViewCreateRequest, ViewResult>(new ViewCreateRequest { Document = Drawing, Sheet = "Details", Model = pin, Orientation = "top", At = new SheetPoint(105, 50), Scale = new ScaleRatio(1, 1) });
        Views["pin"] = view.View.Name;
        var lines = await Call<AnnotationCentreLinesRequest, AnnotationCentresResult>(new AnnotationCentreLinesRequest { Document = Drawing, View = view.View.Name });
        Assert.Equal(1, lines.Eligible);
        var line = Assert.Single(lines.Made);
        Assert.NotEmpty(line.Attached);

        // Round 8 measured SP4 keeping a centre mark on its circle whatever SetPosition2 is given, so annotation.set
        // refuses to move one; it is still where it was, at the hole's centre.
        var stuck = await Refused(() => Call<AnnotationSetRequest, AnnotationResult>(new AnnotationSetRequest { Document = Drawing, Annotation = mark.Reference!, At = new SheetPoint(125, 185) }));
        Assert.Equal(ErrorCode.InvalidArgument, stuck.Code);
        Assert.Equal("/at", stuck.Error.Details!["field"]);
        var listed = (await Call<AnnotationListRequest, AnnotationListResult>(new AnnotationListRequest { Document = Drawing, View = Views["front"] })).Annotations.Single(a => a.Name == mark.Name);
        Near(120, listed.Position!.X, "the centre mark's x after the refused move");
        Near(180, listed.Position.Y, "the centre mark's y after the refused move");
        return $"{mark.Name} at {mark.Position}; again {again.Eligible} circle, {again.Already} already marked; pin top view {view.View.Name} on Details drew {view.View.Extents}; {line.Name} attached to {line.Attached.Count} entities; annotation.set at (125, 185) on it refused ({stuck.Message}), still at {listed.Position}";
    });

    [SkippableFact, Step(25)]
    public Task Datum_A_on_the_base_edge_and_a_position_tolerance_of_the_hole_to_it() => _run.CaseAsync(25, "annotation.datum A on the front's bottom edge at (140, 150), by reference; annotation.gtol position Ø0.1 to A on the hole's edge at (75, 215), its frame read back; a frame to datum B refused (none on the drawing); flatness to A refused (a form tolerance)", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (_, _, bottom, hole) = await FrontEdges();
        var datum = await Call<AnnotationDatumRequest, AnnotationResult>(new AnnotationDatumRequest { Document = Drawing, Label = "A", AttachTo = InFront(bottom), At = new SheetPoint(140, 150) });
        Assert.Equal("A", datum.Annotation.Label);
        Assert.Single(datum.Annotation.Attached);
        var gtol = await Call<AnnotationGtolRequest, AnnotationResult>(new AnnotationGtolRequest
        {
            Document = Drawing, Characteristic = GtolCharacteristic.Position, ToleranceMm = 0.1, Diameter = true, Datums = new List<string> { "A" }, AttachTo = InFront(hole), At = new SheetPoint(75, 215),
        });
        var frame = gtol.Annotation.Gtol!;
        Assert.Equal(GtolCharacteristic.Position, frame.Characteristic);
        Assert.Equal(0.1, frame.ToleranceMm);
        Assert.True(frame.Diameter);
        Assert.Equal(new[] { "A" }, frame.Datums);
        BlockUnchanged(gtol.Change);
        var noB = await Refused(() => Call<AnnotationGtolRequest, AnnotationResult>(new AnnotationGtolRequest { Document = Drawing, Characteristic = GtolCharacteristic.Position, ToleranceMm = 0.1, Datums = new List<string> { "B" }, AttachTo = InFront(hole) }));
        Assert.Equal("/datums/0", noB.Error.Details!["field"]);
        var flatness = await Refused(() => Call<AnnotationGtolRequest, AnnotationResult>(new AnnotationGtolRequest { Document = Drawing, Characteristic = GtolCharacteristic.Flatness, ToleranceMm = 0.05, Datums = new List<string> { "A" }, AttachTo = InFront(bottom) }));
        Assert.Equal("/datums", flatness.Error.Details!["field"]);
        return $"{datum.Annotation.Name} label {datum.Annotation.Label} at {datum.Annotation.Position}; {gtol.Annotation.Name}: {WireNames.ForEnum(frame.Characteristic!.Value)} Ø{frame.ToleranceMm} {WireNames.ForEnum(frame.MaterialCondition ?? MaterialCondition.None)} | {string.Join(" | ", frame.Datums)} at {gtol.Annotation.Position}; B refused ({noB.Message}); flatness to A refused";
    });

    [SkippableFact, Step(26)]
    public Task The_list_reads_everything_and_deletes_leave_the_models_dimension_in_the_model() => _run.CaseAsync(26, "annotation.list of Sheet1 by kind; annotation.delete of the width dimension, the THRU note and an imported model dimension: gone from the drawing, the model keeps its dimension with its value; the THRU note's reference no longer resolves", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var list = await Call<AnnotationListRequest, AnnotationListResult>(new AnnotationListRequest { Document = Drawing, Sheet = "Sheet1" });
        var kinds = list.Annotations.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(kinds.GetValueOrDefault(AnnotationKind.Note) >= 2, "notes");
        Assert.True(kinds.GetValueOrDefault(AnnotationKind.Dimension) >= 4, "dimensions");
        Assert.Equal(1, kinds.GetValueOrDefault(AnnotationKind.CentreMark));
        Assert.Equal(1, kinds.GetValueOrDefault(AnnotationKind.Datum));
        Assert.Equal(1, kinds.GetValueOrDefault(AnnotationKind.Gtol));
        var before = await BlockDimensions();
        var gone = await Call<AnnotationDeleteRequest, AnnotationDeleteResult>(new AnnotationDeleteRequest { Document = Drawing, Annotations = { Made["width"], Made["thru"], Made["imported"] } });
        Assert.Equal(3, gone.Deleted.Count);
        string kept = Assert.Single(gone.ModelDimensionsKept);
        var after = await BlockDimensions();
        Assert.Equal(before.Dimensions.Select(d => (d.Name, d.Value)), after.Dimensions.Select(d => (d.Name, d.Value)));
        var stale = await Refused(() => Call<AnnotationSetRequest, AnnotationResult>(new AnnotationSetRequest { Document = Drawing, Annotation = Made["thru"], At = new SheetPoint(150, 220) }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, stale.Code);
        return $"Sheet1: {string.Join(", ", kinds.Select(k => $"{k.Value} {WireNames.ForEnum(k.Key)}"))}; deleted {string.Join(", ", gone.Deleted)}; the model kept {kept}; the THRU note's reference {WireNames.ForEnum(stale.Code)} ({stale.Error.Details!["reason"]})";
    });

    [SkippableFact, Step(27)]
    public Task SOLIDWORKS_persistent_ids_for_an_annotation_are_probed() => _run.CaseAsync(27, "information only: whether GetPersistReference3 gives an annotation an id (api.call reports an object's reference when it can persist it); annotation references are name-based either way", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        // Reached through the sheet's views, not ActivateView: a view activated by anyone but the add-in's own operations
        // is the user editing it (5.C live run, round 2), and would leave case 28 to find the drawing busy.
        var first = await Call<ApiCallRequest, ApiCallResult>(new ApiCallRequest { Document = Drawing, Target = "document", Member = "GetFirstView.GetNextView.GetFirstAnnotation3", Rebuild = false });
        return $"annotation: {JsonCodec.Serialize(first.Result)} (a ref means GetPersistReference3 gave an id)";
    });

    /// <summary>
    /// Asks the member to do <paramref name="action"/> by hand, waits until diag.editing sees it (<paramref name="open"/>),
    /// then makes a harmless drawing change (a note), which must be refused busy with user_editing before anything is
    /// made. Null when nothing was seen within 3 min.
    /// </summary>
    async Task<(SwAgentException Busy, DiagEditingResult Seen)?> BusyWhile(string action, Func<DiagEditingResult, bool> open)
    {
        _run.Say($"ACTION: {action} (waiting up to 3 min); leave it so until the run asks you to finish");
        var clock = Stopwatch.StartNew();
        DiagEditingResult seen;
        while (!open(seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = Drawing })))
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) return null;
            await Task.Delay(2000);
        }
        int before = (await Call<AnnotationListRequest, AnnotationListResult>(new AnnotationListRequest { Document = Drawing })).Annotations.Count;
        var busy = await Refused(() => Call<AnnotationNoteRequest, AnnotationNoteResult>(new AnnotationNoteRequest { Document = Drawing, Sheet = "Sheet1", Text = "WAIT", At = new SheetPoint(300, 60) }));
        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal("user_editing", busy.Error.Details!["reason"]);
        Assert.Equal("before", busy.Error.Details["detected"]);
        Assert.Equal(before, (await Call<AnnotationListRequest, AnnotationListResult>(new AnnotationListRequest { Document = Drawing })).Annotations.Count);
        return (busy, seen);
    }

    /// <summary>Asks for what <see cref="BusyWhile"/> waited on to be finished, waits until diag.editing sees nothing open, then writes the drawing's Title again, which must work.</summary>
    async Task<TimeSpan> FreeAfter(string action)
    {
        _run.Say($"ACTION: {action} (waiting up to 3 min)");
        var clock = Stopwatch.StartNew();
        DiagEditingResult seen;
        while ((seen = await Call<DiagEditingRequest, DiagEditingResult>(new DiagEditingRequest { Document = Drawing })).Sketch != null || seen.Command != null || seen.SheetFormat)
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) throw _run.Stop($"the user's {(seen.SheetFormat ? "sheet format" : seen.Sketch ?? seen.Command)} was still open after 3 min");
            await Task.Delay(2000);
        }
        await Call<DrawingPropertiesRequest, DrawingPropertiesResult>(new DrawingPropertiesRequest { Document = Drawing, Set = new List<DrawingPropertyValue> { new DrawingPropertyValue { Name = "Title", Value = "SWMCP BLOCK" } } });
        return clock.Elapsed;
    }

    [SkippableFact, Step(28)]
    public Task Editing_the_sheet_format_makes_drawing_changes_wait() => _run.CaseAsync(28, "busy with user_editing, sheet_format, while the user edits Sheet1's sheet format (by hand: Edit Sheet Format), nothing made; works again after Edit Sheet", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        await Call<SheetActivateRequest, SheetActivateResult>(new SheetActivateRequest { Document = Drawing, Sheet = "Sheet1" });
        var (busy, seen) = await BusyWhile("in SOLIDWORKS, in block - Sheet1, right-click an empty part of Sheet1 and choose Edit Sheet Format", s => s.SheetFormat)
            ?? throw new SkipException("no sheet format was seen being edited within 3 min");
        Assert.Equal(true, busy.Error.Details!["sheet_format"]);
        var waited = await FreeAfter("right-click Sheet1 and choose Edit Sheet, to leave the sheet format");
        return $"busy, user_editing, sheet_format, detected before ({busy.Message}); diag.editing saw sketch {seen.Sketch ?? "none"} while it was edited; worked again {waited.TotalSeconds:0} s after asking for Edit Sheet";
    });

    [SkippableFact, Step(29)]
    public Task Editing_a_sketch_in_a_view_makes_drawing_changes_wait() => _run.CaseAsync(29, "busy with user_editing while the user edits a sketch in a view (by hand: Edit Sketch on section line A, or, where SOLIDWORKS offers none, the line tool started in the front view), nothing made; works again once it is closed", async () =>
    {
        await _run.RequireOnlyOwnDocumentsAsync();
        var (busy, seen) = await BusyWhile("in SOLIDWORKS, in the FeatureManager tree under Sheet1 > Section View A-A, right-click Cutting Line A-A and choose Edit Sketch (right-clicking the line on the sheet opens its PropertyManager first, a command, not a sketch edit); if the menu has no Edit Sketch, double-click inside the front view and click Sketch > Line instead, drawing nothing", s => s.Sketch != null || s.Command != null)
            ?? throw new SkipException("no sketch or command was seen open within 3 min");
        string what = seen.Sketch != null ? $"the sketch {seen.Sketch} (Edit Sketch)" : $"the command '{seen.Command}' (not a sketch edit)";
        Assert.True(busy.Error.Details!.ContainsKey("sketch") || busy.Error.Details.ContainsKey("command"), "busy names neither a sketch nor a command");
        var waited = await FreeAfter("exit the sketch (the exit-sketch icon in the corner of the graphics area), or press Esc to end the line tool");
        return $"busy, user_editing, {what}, detected before ({busy.Message}); worked again {waited.TotalSeconds:0} s after asking for it to be closed";
    });

    [SkippableFact, Step(30)]
    public Task Everything_the_suite_made_is_closed() => _run.CaseAsync(30, "the suite's documents closed", async () =>
    {
        var state = await _run.RequireOnlyOwnDocumentsAsync();
        int closed = 0;
        // Drawings first: they unload their hidden models; then whatever still has a window.
        foreach (var d in state.Documents.Where(d => d.Path != null && d.Visible).OrderBy(d => d.Type == DocumentType.Drawing ? 0 : 1))
        {
            if (!(await _run.AddinStateAsync()).Documents.Any(o => o.Path == d.Path)) continue;
            await Call<DocumentCloseRequest, DocumentCloseResult>(new DocumentCloseRequest { Document = d.Path, Discard = true, UsersDocument = true });
            closed++;
        }
        var open = (await _run.AddinStateAsync()).Documents;
        Assert.True(open.Count == 0, $"still open after closing {closed}: {string.Join(", ", open.Select(d => d.Path ?? d.Title))}");
        return $"closed {closed} documents; none open";
    });
}
