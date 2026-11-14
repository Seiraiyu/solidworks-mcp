using System;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the view operations check in a request before SOLIDWORKS is called.</summary>
static class ViewRequests
{
    /// <summary>invalid_argument naming the field unless both numbers of a scale are more than 0.</summary>
    public static void Scale(ScaleRatio? scale, string field)
    {
        if (scale == null) return;
        if (!(scale.Numerator > 0) || double.IsInfinity(scale.Numerator))
            throw Invalid(field + "/numerator", $"{field}: the scale's numerator must be more than 0, not {scale.Numerator}");
        if (!(scale.Denominator > 0) || double.IsInfinity(scale.Denominator))
            throw Invalid(field + "/denominator", $"{field}: the scale's denominator must be more than 0, not {scale.Denominator}");
    }

    /// <summary>invalid_argument naming the field unless the point lies on the sheet.</summary>
    public static void OnSheet(SheetPoint at, SheetFacts sheet, string field)
    {
        if (at.X >= 0 && at.X <= sheet.WidthMm && at.Y >= 0 && at.Y <= sheet.HeightMm) return;
        throw Invalid(field, FormattableString.Invariant($"{field}: ({at.X:0.###}, {at.Y:0.###}) is off the sheet {sheet.Name}, which is {sheet.WidthMm:0.###} x {sheet.HeightMm:0.###} mm (sheet mm from its lower-left corner)"));
    }

    public static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });
}

public sealed class ViewProjectOperation : DrawingChangeOperation<ViewProjectRequest, ViewResult>
{
    /// <summary>The gap between a parent's drawn edges and a projected view placed beside it, sheet mm.</summary>
    public const double GapMm = 20;

    public ViewProjectOperation(AddInServices services)
        : base(services)
    {
    }

    string _parent = "";
    SheetPoint _at = new SheetPoint();

    protected override string? DocumentOf(ViewProjectRequest request) => request.Document;

    protected override ViewResult Change(ISwDocument doc, ISwDrawing drawing, ViewProjectRequest request, ModelTransaction tx, IOperationContext context)
    {
        _parent = View(doc, drawing, request.Parent, "/parent");
        var parent = drawing.ReadView(_parent)!;
        if (parent.Type != ViewType.Model && parent.Type != ViewType.Projected)
        {
            throw ViewRequests.Invalid("/parent",
                $"/parent: {_parent} is a {WireNames.ForEnum(parent.Type)} view; project from a model view or a projected view");
        }
        var t = drawing.Transform(_parent) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no transform for {_parent}");
        var sheet = drawing.ReadSheet(parent.Sheet)!;
        bool horizontal = ViewGeometry.Horizontal(request.Direction);
        if (request.At is SheetPoint asked)
        {
            ViewRequests.OnSheet(asked, sheet, "/at");
            if (ViewGeometry.WrongSide(parent.Position, asked, request.Direction, sheet.Projection) is string side)
                throw ViewRequests.Invalid("/at", $"/at: {side}; {_parent} is centred at {parent.Position}");
            double aligned = horizontal ? asked.Y - parent.Position.Y : asked.X - parent.Position.X;
            if (Math.Abs(aligned) > ViewGeometry.PositionToleranceMm)
            {
                string axis = horizontal ? "y" : "x";
                throw ViewRequests.Invalid("/at/" + axis, FormattableString.Invariant(
                    $"/at/{axis}: a view projected {WireNames.ForEnum(request.Direction)} shares {_parent}'s {(horizontal ? "horizontal" : "vertical")} centre line, {axis} = {(horizontal ? parent.Position.Y : parent.Position.X):0.###} mm; give that {axis} or leave at out"));
            }
            _at = asked;
        }
        else
        {
            var box = drawing.ModelOf(_parent)?.BoundingBox(precise: true)
                ?? throw ViewRequests.Invalid("/at", $"/at: {_parent}'s model gives no bounding box to place the view beside it by; give at");
            var extents = DrawingReader.Extents(drawing, parent) ?? ViewGeometry.Project(t, box);
            _at = ViewGeometry.ProjectedPlace(parent.Position, extents, ViewGeometry.Depth(t, box), request.Direction, sheet.Projection, GapMm);
            ViewRequests.OnSheet(_at, sheet, "/at");
        }
        string name = drawing.ProjectView(_parent, _at)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no view projected from {_parent}", new JsonObject { ["parent"] = _parent });
        MadeView(doc, drawing, tx, name);
        context.Log.Info($"view.project: {name} from {_parent}, seen from {WireNames.ForEnum(request.Direction)} ({WireNames.ForEnum(sheet.Projection)}) at {_at}");
        return new ViewResult { View = new ViewInfo { Name = name, Sheet = parent.Sheet }, ModelUnsaved = drawing.ModelOf(_parent)?.Info().Dirty == true };
    }

    /// <summary>A projected view of the parent where asked, at its scale, turned toward the side asked and on its centre line, drawing the model's projected box.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewProjectRequest request, ViewResult result)
    {
        string name = result.View.Name;
        var facts = drawing.ReadView(name) ?? throw ViewCreateOperation.Failed(name, "exists", "the view on its sheet", "no such view");
        var parent = drawing.ReadView(_parent)!;
        if (facts.Type != ViewType.Projected) throw ViewCreateOperation.Failed(name, "type", "projected", WireNames.ForEnum(facts.Type));
        if (facts.Parent != _parent) throw ViewCreateOperation.Failed(name, "parent", _parent, facts.Parent ?? "none");
        if (!ViewGeometry.Near(facts.Position, _at)) throw ViewCreateOperation.Failed(name, "position", _at.ToString(), facts.Position.ToString());
        if (!DrawingReader.SameScale(facts.Scale, parent.Scale)) throw ViewCreateOperation.Failed(name, "scale", $"{_parent}'s, {parent.Scale}", facts.Scale.ToString());
        var parentT = drawing.Transform(_parent)!;
        var t = ViewChecks.Scale(drawing, name, parentT.Scale);
        if (ViewGeometry.AlignmentMismatch(parentT, t, request.Direction) is string why) throw ViewCreateOperation.Failed(name, "alignment", $"aligned with {_parent}, seen from {WireNames.ForEnum(request.Direction)}", why);
        ViewChecks.Drawn(drawing, name, parentT.Scale, null);
        result.View = Describe(doc, drawing, name);
    }
}

public sealed class ViewSetOperation : DrawingChangeOperation<ViewSetRequest, ViewResult>
{
    public ViewSetOperation(AddInServices services)
        : base(services)
    {
    }

    ViewFacts _old = new ViewFacts();
    SheetPoint _from = new SheetPoint();
    SheetPoint _at = new SheetPoint();
    SheetBox? _drawn;

    protected override string? DocumentOf(ViewSetRequest request) => request.Document;

    /// <summary>A view aligned with the one it was made from: projected and section views (SOLIDWORKS aligns both and draws them at its scale).</summary>
    static bool Aligned(ViewFacts view) => (view.Type == ViewType.Projected || view.Type == ViewType.Section) && view.Parent != null;

    protected override ViewResult Change(ISwDocument doc, ISwDrawing drawing, ViewSetRequest request, ModelTransaction tx, IOperationContext context)
    {
        string name = View(doc, drawing, request.View, "/view");
        var old = drawing.ReadView(name)!;
        _old = old;
        if (request.At == null && request.Scale == null && request.SheetScale == null && request.Display == null && request.Configuration == null)
            throw ViewRequests.Invalid("/view", "view.set changes nothing: give at, scale, sheet_scale, display or configuration");
        if (request.Scale != null && request.SheetScale != null) throw ViewRequests.Invalid("/sheet_scale", "/sheet_scale: give scale or sheet_scale, not both");
        if (request.SheetScale == false) throw ViewRequests.Invalid("/sheet_scale", "/sheet_scale: false is not a scale; give scale for the view's own");
        ViewRequests.Scale(request.Scale, "/scale");
        bool projected = Aligned(old);
        if (projected && (request.Scale != null || request.SheetScale != null))
            throw ViewRequests.Invalid(request.Scale != null ? "/scale" : "/sheet_scale", $"{name} is {(old.Type == ViewType.Section ? "cut" : "projected")} from {old.Parent} and drawn at its scale; rescale {old.Parent}");
        _drawn = old.Type == ViewType.Model || old.Type == ViewType.Projected ? null : DrawingReader.Extents(drawing, old);
        if (old.Type != ViewType.Model && request.Configuration != null)
            throw ViewRequests.Invalid("/configuration", $"/configuration: {name} is a {WireNames.ForEnum(old.Type)} view and shows its parent's configuration; change {old.Parent ?? "its parent"}'s");
        if (request.Configuration != null && request.Configuration.Trim().Length == 0) throw ViewRequests.Invalid("/configuration", "/configuration: give a configuration's name");
        // Where it is and goes: its position; for a detail, where it draws its circle's centre (DrawingReader.Placed).
        _from = DrawingReader.Placed(drawing, old);
        _at = request.At ?? _from;
        if (request.At != null)
        {
            ViewRequests.OnSheet(request.At, drawing.ReadSheet(old.Sheet)!, "/at");
            if (projected && Alignment(drawing, old.Parent!, name) is bool horizontal)
            {
                double off = horizontal ? request.At.Y - old.Position.Y : request.At.X - old.Position.X;
                if (Math.Abs(off) > ViewGeometry.PositionToleranceMm)
                {
                    string axis = horizontal ? "y" : "x";
                    throw ViewRequests.Invalid("/at/" + axis, FormattableString.Invariant(
                        $"/at/{axis}: {name} is aligned with {old.Parent} and moves only along its {(horizontal ? "horizontal" : "vertical")} centre line, {axis} = {(horizontal ? old.Position.Y : old.Position.X):0.###} mm (SOLIDWORKS would keep that {axis} and say nothing)"));
                }
            }
        }

        if (request.Configuration != null && !string.Equals(request.Configuration, old.Configuration, StringComparison.Ordinal))
        {
            drawing.SetConfiguration(name, request.Configuration);
            tx.Edited(new ChangedItem { Name = name, Property = "configuration", Before = old.Configuration ?? "", After = request.Configuration }, () =>
            {
                if (old.Configuration != null) drawing.SetConfiguration(name, old.Configuration);
            });
        }
        if (request.Scale != null || request.SheetScale == true)
        {
            drawing.SetScale(name, request.Scale);
            tx.Edited(new ChangedItem { Name = name, Property = "scale", Before = Scale(old), After = request.Scale?.ToString() ?? "the sheet's" }, () =>
            {
                drawing.SetScale(name, old.SheetScale ? null : old.Scale);
                if (!(drawing.ReadView(name) is ViewFacts back) || !DrawingReader.SameScale(back.Scale, old.Scale))
                    throw new SwAgentException(ErrorCode.OperationFailed, $"{name} did not take its old scale {Scale(old)} back");
            });
        }
        if (request.Display != null)
        {
            drawing.SetDisplay(name, request.Display.Value);
            tx.Edited(new ChangedItem { Name = name, Property = "display", Before = old.Display == null ? "" : WireNames.ForEnum(old.Display.Value), After = WireNames.ForEnum(request.Display.Value) }, () =>
            {
                if (old.Display != null) drawing.SetDisplay(name, old.Display.Value);
            });
        }
        if (request.At != null || request.Scale != null || request.SheetScale == true)
        {
            if (old.Type == ViewType.Detail) MoveDetail(drawing, name, _at);
            else drawing.SetPosition(name, _at);
            tx.Edited(new ChangedItem { Name = name, Property = "position", Before = _from.ToString(), After = _at.ToString() }, () =>
            {
                drawing.SetPosition(name, old.Position);
                if (!(drawing.ReadView(name) is ViewFacts back) || !ViewGeometry.Near(back.Position, old.Position))
                    throw new SwAgentException(ErrorCode.OperationFailed, $"{name} did not go back to {old.Position}");
            });
        }
        context.Log.Info($"view.set: {name}: {string.Join(", ", tx.Changed.Select(c => $"{c.Property} {c.Before} -> {c.After}"))}");
        return new ViewResult { View = new ViewInfo { Name = name, Sheet = old.Sheet }, ModelUnsaved = drawing.ModelOf(name)?.Info().Dirty == true };
    }

    /// <summary>Every value reads back; what the view draws is its model's box at its scale; the views projected from it are still aligned.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewSetRequest request, ViewResult result)
    {
        string name = result.View.Name;
        var facts = drawing.ReadView(name) ?? throw ViewCreateOperation.Failed(name, "exists", "the view on its sheet", "no such view");
        if (request.Configuration != null && !string.Equals(facts.Configuration, request.Configuration, StringComparison.OrdinalIgnoreCase))
            throw ViewCreateOperation.Failed(name, "configuration", request.Configuration, facts.Configuration ?? "none");
        if (request.Display != null && facts.Display != request.Display)
            throw ViewCreateOperation.Failed(name, "display", WireNames.ForEnum(request.Display.Value), facts.Display == null ? "a mode the add-in does not name" : WireNames.ForEnum(facts.Display.Value));
        var placed = DrawingReader.Placed(drawing, facts);
        if (!ViewGeometry.Near(placed, _at))
            throw ViewCreateOperation.Failed(name, "position", facts.Type == ViewType.Detail ? $"the circle's centre drawn at {_at}" : _at.ToString(),
                facts.Type == ViewType.Detail ? $"{placed} (its position {facts.Position})" : facts.Position.ToString());
        var scale = request.Scale ?? (request.SheetScale == true ? drawing.ReadSheet(facts.Sheet)!.Scale : _old.Scale);
        if (!DrawingReader.SameScale(facts.Scale, scale) || (request.Scale != null && facts.SheetScale) || (request.SheetScale == true && !facts.SheetScale))
            throw ViewCreateOperation.Failed(name, "scale", request.SheetScale == true ? $"the sheet's, {scale}" : scale.ToString(), $"{facts.Scale}{(facts.SheetScale ? " (the sheet's)" : "")}");
        if (facts.Type == ViewType.Model || facts.Type == ViewType.Projected)
        {
            string? orientation = facts.Orientation != null && ViewGeometry.Standard.Contains(facts.Orientation) && facts.Type == ViewType.Model ? facts.Orientation : null;
            ViewChecks.Drawn(drawing, name, DrawingReader.Ratio(scale), orientation);
        }
        else
        {
            // A section, detail or flat pattern draws what it drew, moved and rescaled about where it is.
            ViewChecks.Scale(drawing, name, DrawingReader.Ratio(scale));
            var now = DrawingReader.Extents(drawing, facts);
            if (_drawn != null)
            {
                double k = DrawingReader.Ratio(scale) / DrawingReader.Ratio(_old.Scale);
                var expected = new SheetBox
                {
                    Min = new SheetPoint(_at.X + (_drawn.Min.X - _from.X) * k, _at.Y + (_drawn.Min.Y - _from.Y) * k),
                    Max = new SheetPoint(_at.X + (_drawn.Max.X - _from.X) * k, _at.Y + (_drawn.Max.Y - _from.Y) * k),
                };
                if (now == null || !ViewGeometry.SameBox(expected, now))
                    throw ViewCreateOperation.Failed(name, "extents", $"{expected} (what it drew, moved and rescaled)", now?.ToString() ?? "nothing drawn");
            }
        }
        foreach (var child in drawing.SheetNames().SelectMany(drawing.ViewNames).Select(drawing.ReadView).Where(v => v != null && v.Parent == name && (v.Type == ViewType.Projected || v.Type == ViewType.Section)))
        {
            if (Alignment(drawing, name, child!.Name) == null)
                throw ViewCreateOperation.Failed(child.Name, "alignment", $"on {name}'s centre lines", "off both: SOLIDWORKS did not keep it aligned");
        }
        result.View = Describe(doc, drawing, name);
    }

    /// <summary>
    /// Moves a detail so it draws its circle's centre at <paramref name="at"/>: its Position by how far that centre is from
    /// <paramref name="at"/>, then once more if SOLIDWORKS drew it off (as view.detail does; 5.B live run).
    /// </summary>
    static void MoveDetail(ISwDrawing drawing, string name, SheetPoint at)
    {
        for (int i = 0; i < 2; i++)
        {
            var facts = drawing.ReadView(name);
            if (facts == null) return;
            var now = DrawingReader.Placed(drawing, facts);
            if (i > 0 && ViewGeometry.Near(now, at)) return;
            drawing.SetPosition(name, new SheetPoint(facts.Position.X + at.X - now.X, facts.Position.Y + at.Y - now.Y));
        }
    }

    /// <summary>How a projected view is aligned with its parent: true on its horizontal centre line, false on its vertical one, null on neither (the model's origin drawn at the same y, or x, in both).</summary>
    static bool? Alignment(ISwDrawing drawing, string parent, string child)
    {
        if (!(drawing.Transform(parent) is ViewTransform p) || !(drawing.Transform(child) is ViewTransform c)) return null;
        var origin = new Point3(0, 0, 0);
        var a = ViewGeometry.ToSheet(p, origin);
        var b = ViewGeometry.ToSheet(c, origin);
        if (Math.Abs(a.Y - b.Y) <= ViewGeometry.PositionToleranceMm) return true;
        if (Math.Abs(a.X - b.X) <= ViewGeometry.PositionToleranceMm) return false;
        return null;
    }

    static string Scale(ViewFacts v) => v.SheetScale ? $"the sheet's, {v.Scale}" : v.Scale.ToString();
}

public sealed class ViewDeleteOperation : DrawingChangeOperation<ViewDeleteRequest, ViewDeleteResult>
{
    public ViewDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    System.Collections.Generic.List<string> _kept = new System.Collections.Generic.List<string>();

    protected override string? DocumentOf(ViewDeleteRequest request) => request.Document;

    protected override ViewDeleteResult Change(ISwDocument doc, ISwDrawing drawing, ViewDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Views.Count == 0) throw ViewRequests.Invalid("/views", "/views: name at least one view to delete");
        var views = new System.Collections.Generic.List<string>();
        for (int i = 0; i < request.Views.Count; i++)
        {
            string name = View(doc, drawing, request.Views[i], $"/views/{i}");
            if (views.Contains(name)) throw ViewRequests.Invalid($"/views/{i}", $"/views/{i}: {name} is named twice");
            views.Add(name);
        }
        var all = drawing.SheetNames().SelectMany(drawing.ViewNames).Select(drawing.ReadView).Where(v => v != null).Select(v => v!).ToList();
        for (int i = 0; i < views.Count; i++)
        {
            var dependents = all.Where(v => v.Parent == views[i] && !views.Contains(v.Name)).Select(v => v.Name).ToList();
            if (dependents.Count == 0) continue;
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"/views/{i}: {string.Join(", ", dependents)} {(dependents.Count == 1 ? "was" : "were")} made from {views[i]}; delete {(dependents.Count == 1 ? "it" : "them")} in the same request, or keep {views[i]}",
                new JsonObject { ["field"] = $"/views/{i}", ["view"] = views[i], ["dependents"] = dependents.Select(d => (object?)d).ToList() });
        }
        _kept = all.Select(v => v.Name).Except(views).ToList();
        // A section or detail view's line or circle stays in a parent that is kept: it goes with the view (by selection).
        _cuts = all.Where(v => views.Contains(v.Name) && v.Parent != null && _kept.Contains(v.Parent))
            .Select(v => (View: v.Name, Parent: v.Parent!, Label: drawing.ReadSection(v.Name)?.Label ?? drawing.ReadDetail(v.Name)?.Label, Section: v.Type == ViewType.Section))
            .Where(c => c.Label != null).Select(c => (c.View, c.Parent, c.Label!, c.Section)).ToList();
        if (!drawing.DeleteViews(views)) throw Partly(drawing, views);
        DeleteCuts(drawing, _cuts);
        context.Log.Info($"view.delete: {string.Join(", ", views)}{(_cuts.Count > 0 ? "; with " + string.Join(", ", _cuts.Select(c => $"{(c.Section ? "section line" : "detail circle")} {c.Label} of {c.Parent}")) : "")}");
        return new ViewDeleteResult { Deleted = views };
    }

    System.Collections.Generic.List<(string View, string Parent, string Label, bool Section)> _cuts = new System.Collections.Generic.List<(string, string, string, bool)>();

    /// <summary>Deletes each section line or detail circle still in its parent.</summary>
    static void DeleteCuts(ISwDrawing drawing, System.Collections.Generic.IEnumerable<(string View, string Parent, string Label, bool Section)> cuts)
    {
        foreach (var (_, parent, label, section) in cuts.Where(c => drawing.CutLabels(c.Parent).Contains(c.Label, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            if (section) drawing.DeleteSectionLine(parent, label);
            else drawing.DeleteDetailCircle(parent, label);
        }
    }

    /// <summary>
    /// What to answer when SOLIDWORKS refused a delete: it deletes a sheet at a time, so the views on sheets done before
    /// may be gone. Read again: none gone is a plain operation_failed; some gone cannot be put back, so it is
    /// operation_failed saying which views were deleted and which remain (rollback_failed, as a rollback that could not
    /// finish), with the lines and circles of those deleted taken from their parents.
    /// </summary>
    SwAgentException Partly(ISwDrawing drawing, System.Collections.Generic.List<string> views)
    {
        var now = drawing.SheetNames().SelectMany(drawing.ViewNames).ToList();
        var deleted = views.Where(v => !now.Contains(v)).ToList();
        var remaining = views.Where(now.Contains).ToList();
        if (deleted.Count == 0)
            return new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", views)}; none was deleted", new JsonObject { ["views"] = views.Select(v => (object?)v).ToList() });
        DeleteCuts(drawing, _cuts.Where(c => deleted.Contains(c.View)));
        return new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS deleted {string.Join(", ", deleted)} but not {string.Join(", ", remaining)} (it deletes a sheet's views at a time); a deleted view cannot be put back, so the drawing is left with unsaved changes for you to inspect (drawing.read); it was not closed or reverted",
            new JsonObject
            {
                ["views"] = views.Select(v => (object?)v).ToList(),
                ["deleted"] = deleted.Select(v => (object?)v).ToList(),
                ["remaining"] = remaining.Select(v => (object?)v).ToList(),
                ["rollback_failed"] = true,
                ["cause"] = WireNames.ForEnum(ErrorCode.OperationFailed),
            });
    }

    /// <summary>Each deleted view is gone, with any section line or detail circle it had in a parent kept, and each other view is still there.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewDeleteRequest request, ViewDeleteResult result)
    {
        var now = drawing.SheetNames().SelectMany(drawing.ViewNames).ToList();
        var left = result.Deleted.Where(now.Contains).ToList();
        left.AddRange(_cuts.Where(c => drawing.CutLabels(c.Parent).Contains(c.Label, StringComparer.OrdinalIgnoreCase))
            .Select(c => $"{(c.Section ? "section line" : "detail circle")} {c.Label} of {c.Parent}"));
        var lost = _kept.Where(k => !now.Contains(k)).ToList();
        if (left.Count == 0 && lost.Count == 0) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            left.Count > 0 ? $"SOLIDWORKS reported the views deleted, but {string.Join(", ", left)} {(left.Count == 1 ? "is" : "are")} still there" : $"deleting went too far: {string.Join(", ", lost)} went too",
            new JsonObject { ["check"] = "views", ["left"] = left.Select(l => (object?)l).ToList(), ["lost"] = lost.Select(l => (object?)l).ToList() });
    }
}

public sealed class ViewEntitiesOperation : Operation<ViewEntitiesRequest, ViewEntitiesResult>
{
    readonly AddInServices _services;

    public ViewEntitiesOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ViewEntitiesResult Run(ViewEntitiesRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        string view = _services.References.ResolveView(doc, drawing, request.View, "/view");
        var model = drawing.ModelOf(view)
            ?? throw ViewRequests.Invalid("/view", $"/view: {view} shows no model that is loaded, so it shows no entity");
        var info = model.Info();
        if (info.Type != DocumentType.Part)
            throw ViewRequests.Invalid("/view", $"/view: view.entities lists the entities of a view of a part, and {view} shows {info.Title}, an assembly");
        var t = drawing.Transform(view) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no transform for {view}");
        var kinds = request.Kinds == null || request.Kinds.Count == 0 ? new[] { ViewEntityKind.Edge, ViewEntityKind.Vertex }.ToList() : request.Kinds;
        var result = new ViewEntitiesResult { View = view, Model = info.Path ?? info.Title, Scale = t.Scale };
        foreach (var e in drawing.VisibleEntities(view, kinds.Contains(ViewEntityKind.Edge), kinds.Contains(ViewEntityKind.Vertex)).OrderBy(e => e.Vertex))
        {
            var described = _services.References.Describe(model, e.Entity);
            var drawn = ViewGeometry.Draw(t, described, e.Vertex);
            drawn.Reference = described.Reference;
            drawn.Kind = e.Vertex ? ReferenceKind.Vertex : ReferenceKind.Edge;
            drawn.Silhouette = e.Silhouette;
            described.Reference = null;
            drawn.Entity = described;
            result.Entities.Add(drawn);
        }
        context.Log.Info($"view.entities: {view}: {result.Entities.Count} entities, {result.Entities.Count(x => x.Reference != null)} with a model reference");
        return result;
    }

    /// <summary>The view still shows as many entities: nothing changed it while it was read.</summary>
    protected override void Verify(ViewEntitiesRequest request, ViewEntitiesResult result, IOperationContext context)
    {
        var (_, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var kinds = request.Kinds == null || request.Kinds.Count == 0 ? new[] { ViewEntityKind.Edge, ViewEntityKind.Vertex }.ToList() : request.Kinds;
        int now = drawing.ReadView(result.View) == null ? -1 : drawing.VisibleEntities(result.View, kinds.Contains(ViewEntityKind.Edge), kinds.Contains(ViewEntityKind.Vertex)).Count;
        if (now != result.Entities.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"{result.View} changed while it was read ({result.Entities.Count} entities, now {(now < 0 ? "no view" : now.ToString())}); ask again",
                new JsonObject { ["check"] = "unchanged" });
        }
    }
}

public sealed class ViewCreateOperation : DrawingChangeOperation<ViewCreateRequest, ViewResult>
{
    public ViewCreateOperation(AddInServices services)
        : base(services)
    {
    }

    string _model = "";
    ViewDisplay _display;

    protected override string? DocumentOf(ViewCreateRequest request) => request.Document;

    protected override ViewResult Change(ISwDocument doc, ISwDrawing drawing, ViewCreateRequest request, ModelTransaction tx, IOperationContext context)
    {
        _model = SavedModels.Require(Services.SolidWorks, request.Model, "/model", out var open);
        string orientation = request.Orientation.Trim();
        if (orientation.Length == 0) throw ViewRequests.Invalid("/orientation", $"/orientation: give {string.Join(", ", ViewGeometry.Standard)} or the name of a view the model has");
        if (ViewGeometry.Standard.Contains(orientation.ToLowerInvariant())) orientation = orientation.ToLowerInvariant();
        ViewRequests.Scale(request.Scale, "/scale");
        if (request.Configuration != null && request.Configuration.Trim().Length == 0) throw ViewRequests.Invalid("/configuration", "/configuration: give a configuration's name, or leave it out for the model's active one");
        string sheet = request.Sheet != null ? Sheet(doc, drawing, request.Sheet, "/sheet") : drawing.ActiveSheet() ?? drawing.SheetNames().First();
        var facts = drawing.ReadSheet(sheet)!;
        ViewRequests.OnSheet(request.At, facts, "/at");
        bool part = PathRules.TypeOf(_model) == DocumentType.Part;
        _display = request.Display ?? (part && ViewGeometry.Orthographic.ContainsKey(orientation) ? ViewDisplay.HiddenLinesVisible : ViewDisplay.HiddenLinesRemoved);

        if (drawing.ActiveSheet() != sheet && !drawing.ActivateSheet(sheet))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not show the sheet {sheet} to put the view on", new JsonObject { ["sheet"] = sheet });
        string name = drawing.CreateModelView(_model, orientation, request.At)
            ?? throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS made no view of {Path.GetFileName(_model)} ({orientation}); if '{orientation}' is not front, back, left, right, top, bottom, isometric, dimetric or trimetric, the model may have no view of that name",
                new JsonObject { ["model"] = _model, ["orientation"] = orientation });
        MadeView(doc, drawing, tx, name);
        if (request.Configuration != null) drawing.SetConfiguration(name, request.Configuration);
        drawing.SetScale(name, request.Scale);
        drawing.SetDisplay(name, _display);
        drawing.SetPosition(name, request.At);
        context.Log.Info($"view.create: {name} of {_model} ({orientation}) on {sheet} at {request.At}, scale {(request.Scale?.ToString() ?? "the sheet's")}, {WireNames.ForEnum(_display)}");
        return new ViewResult { View = new ViewInfo { Name = name, Sheet = sheet }, ModelUnsaved = open?.Info().Dirty == true };
    }

    /// <summary>Design 611, How a view is checked: on its sheet with the model, configuration and display asked, centred and scaled as asked, facing the orientation asked, drawing the model's projected box.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewCreateRequest request, ViewResult result)
    {
        string name = result.View.Name;
        var facts = drawing.ReadView(name) ?? throw Failed(name, "exists", "the view on its sheet", "no such view");
        if (facts.Sheet != result.View.Sheet) throw Failed(name, "sheet", result.View.Sheet, facts.Sheet);
        if (facts.Type != ViewType.Model) throw Failed(name, "type", "model", WireNames.ForEnum(facts.Type));
        if (facts.Model == null || !DocumentKeys.Same(Path.GetFullPath(facts.Model), _model)) throw Failed(name, "model", _model, facts.Model ?? "none");
        if (request.Configuration != null && !string.Equals(facts.Configuration, request.Configuration, StringComparison.OrdinalIgnoreCase))
            throw Failed(name, "configuration", request.Configuration, facts.Configuration ?? "none");
        if (facts.Display != _display) throw Failed(name, "display", WireNames.ForEnum(_display), facts.Display == null ? "a mode the add-in does not name" : WireNames.ForEnum(facts.Display.Value));
        if (!ViewGeometry.Near(facts.Position, request.At)) throw Failed(name, "position", request.At.ToString(), facts.Position.ToString());
        var sheetScale = drawing.ReadSheet(facts.Sheet)!.Scale;
        var scale = request.Scale ?? sheetScale;
        if (facts.SheetScale != (request.Scale == null) || !DrawingReader.SameScale(facts.Scale, scale))
            throw Failed(name, "scale", request.Scale == null ? $"the sheet's, {sheetScale}" : scale.ToString(), $"{facts.Scale}{(facts.SheetScale ? " (the sheet's)" : "")}");
        string orientation = request.Orientation.Trim().ToLowerInvariant();
        ViewChecks.Drawn(drawing, name, DrawingReader.Ratio(scale), ViewGeometry.Standard.Contains(orientation) ? orientation : null);
        result.View = Describe(doc, drawing, name);
    }

    internal static SwAgentException Failed(string view, string check, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"the view {view} failed its {check} check: expected {expected}, measured {measured}",
            new JsonObject { ["check"] = check, ["view"] = view, ["expected"] = expected, ["measured"] = measured });
}
