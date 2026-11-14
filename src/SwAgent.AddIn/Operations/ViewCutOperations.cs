using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the section and detail operations share: the parent they cut from, and the labels.</summary>
static class ViewCuts
{
    /// <summary>
    /// The parent named, which must be a model or projected view (or, when <paramref name="sections"/>, a section view)
    /// showing a loaded model; with its facts, model and transform.
    /// </summary>
    public static (ViewFacts Facts, ISwDocument Model, ViewTransform Transform) Parent(ISwDrawing drawing, string parent, string what, bool sections = false)
    {
        var facts = drawing.ReadView(parent)!;
        if (facts.Type != ViewType.Model && facts.Type != ViewType.Projected && !(sections && facts.Type == ViewType.Section))
            throw ViewRequests.Invalid("/parent", $"/parent: {parent} is a {WireNames.ForEnum(facts.Type)} view; {what} a model view{(sections ? ", a projected view or a section view" : " or a projected view")}");
        var model = drawing.ModelOf(parent) ?? throw ViewRequests.Invalid("/parent", $"/parent: {parent} shows no model that is loaded");
        var t = drawing.Transform(parent) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no transform for {parent}");
        return (facts, model, t);
    }

    /// <summary>Every section's and detail's label in the drawing, as SOLIDWORKS reads them.</summary>
    public static List<string> LabelsInUse(ISwDrawing drawing) =>
        drawing.SheetNames().SelectMany(drawing.ViewNames)
            .Select(v => drawing.ReadSection(v)?.Label ?? drawing.ReadDetail(v)?.Label)
            .Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).ToList();

    /// <summary>The label asked (letters and digits, up to 4, not another section's or detail's), or the next free one.</summary>
    public static string Label(ISwDrawing drawing, string? asked)
    {
        var used = LabelsInUse(drawing);
        if (asked == null) return ViewCutGeometry.NextLabel(used);
        string label = asked.Trim();
        if (label.Length == 0 || label.Length > 4 || !label.All(char.IsLetterOrDigit))
            throw ViewRequests.Invalid("/label", $"/label: a label is 1 to 4 letters or digits (A, B2), not '{asked}'");
        if (used.Contains(label, StringComparer.OrdinalIgnoreCase))
            throw ViewRequests.Invalid("/label", $"/label: {label} already labels a section or detail of this drawing; give another, or leave it out for {ViewCutGeometry.NextLabel(used)}");
        return label;
    }

    /// <summary>Shows the parent's sheet, which a section or detail is put on.</summary>
    public static void Show(ISwDrawing drawing, string sheet)
    {
        if (drawing.ActiveSheet() != sheet && !drawing.ActivateSheet(sheet))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not show the sheet {sheet} to put the view on", new JsonObject { ["sheet"] = sheet });
    }
}

public sealed class ViewSectionOperation : DrawingChangeOperation<ViewSectionRequest, ViewSectionResult>
{
    /// <summary>The gap between a parent's drawn edges and a section placed beside it, sheet mm.</summary>
    public const double GapMm = 20;

    public ViewSectionOperation(AddInServices services)
        : base(services)
    {
    }

    string _parent = "";
    SheetPoint _at = new SheetPoint();

    protected override string? DocumentOf(ViewSectionRequest request) => request.Document;

    protected override ViewSectionResult Change(ISwDocument doc, ISwDrawing drawing, ViewSectionRequest request, ModelTransaction tx, IOperationContext context)
    {
        _parent = View(doc, drawing, request.Parent, "/parent");
        var (parent, model, t) = ViewCuts.Parent(drawing, _parent, "cut a section from");
        var cut = request.Cut ?? throw ViewRequests.Invalid("/cut", "/cut: say where the section line goes");
        var through = ModelPlaces.Resolve(Services.References, model, cut.Through ?? new ModelPlace(), "/cut/through");
        var extents = DrawingReader.Extents(drawing, parent) ?? throw ViewRequests.Invalid("/parent", $"/parent: {_parent} draws nothing to cut");
        var line = ViewCutGeometry.CutLine(t, extents, through.Point, cut.Orientation);
        if (line == null)
        {
            throw ViewRequests.Invalid("/cut/through",
                $"/cut/through: {through.Described} is drawn at {ViewGeometry.ToSheet(t, through.Point)} in {_parent}, on or outside what it draws ({extents}) across a {WireNames.ForEnum(cut.Orientation)} line, so the line would cut nothing");
        }
        string label = ViewCuts.Label(drawing, request.Label);
        var sheet = drawing.ReadSheet(parent.Sheet)!;
        bool sharesX = ViewCutGeometry.SharesX(cut.Orientation);
        if (request.At is SheetPoint asked)
        {
            ViewRequests.OnSheet(asked, sheet, "/at");
            double off = sharesX ? asked.X - parent.Position.X : asked.Y - parent.Position.Y;
            if (Math.Abs(off) > ViewGeometry.PositionToleranceMm)
            {
                string axis = sharesX ? "x" : "y";
                throw ViewRequests.Invalid("/at/" + axis, FormattableString.Invariant(
                    $"/at/{axis}: a section cut {WireNames.ForEnum(cut.Orientation)} across {_parent} shares its {(sharesX ? "vertical" : "horizontal")} centre line, {axis} = {(sharesX ? parent.Position.X : parent.Position.Y):0.###} mm; give that {axis} or leave at out"));
            }
            _at = asked;
        }
        else
        {
            var box = model.BoundingBox(precise: true)
                ?? throw ViewRequests.Invalid("/at", $"/at: {_parent}'s model gives no bounding box to place the section beside it by; give at");
            _at = ViewCutGeometry.SectionPlace(parent.Position, extents, ViewGeometry.Depth(t, box), cut.Orientation, GapMm);
            ViewRequests.OnSheet(_at, sheet, "/at");
        }

        ViewCuts.Show(drawing, parent.Sheet);
        string name = drawing.CutSection(_parent, line.Value.From, line.Value.To, _at, label, request.Reversed == true)
            ?? throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS made no section of {_parent} through {through.Described}; nothing was left on the drawing",
                new JsonObject { ["parent"] = _parent, ["label"] = label });
        MadeCut(doc, drawing, tx, name, _parent, label, section: true);
        context.Log.Info($"view.section: {name} ({label}-{label}) of {_parent} through {through.Described}, a {WireNames.ForEnum(cut.Orientation)} line {line.Value.From}..{line.Value.To}, at {_at}");
        return new ViewSectionResult
        {
            View = new ViewInfo { Name = name, Sheet = parent.Sheet },
            Label = label,
            Reversed = request.Reversed == true,
            Through = through.Point,
            ThroughIs = through.Described,
            ModelUnsaved = model.Info().Dirty,
        };
    }

    /// <summary>
    /// Design 611, Sections and details: a section of the parent with the label and direction asked, aligned with the
    /// parent at its scale, whose hatched faces lie in the plane through the point asked, drawing inside the model's box.
    /// </summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewSectionRequest request, ViewSectionResult result)
    {
        string name = result.View.Name;
        var facts = drawing.ReadView(name) ?? throw ViewCreateOperation.Failed(name, "exists", "the view on its sheet", "no such view");
        if (facts.Type != ViewType.Section) throw ViewCreateOperation.Failed(name, "type", "section", WireNames.ForEnum(facts.Type));
        if (facts.Parent != _parent) throw ViewCreateOperation.Failed(name, "parent", _parent, facts.Parent ?? "none");
        var section = drawing.ReadSection(name) ?? throw ViewCreateOperation.Failed(name, "section", $"the section {result.Label}", "no section");
        if (!string.Equals(section.Label, result.Label, StringComparison.OrdinalIgnoreCase)) throw ViewCreateOperation.Failed(name, "label", result.Label, section.Label);
        if (section.Reversed != result.Reversed) throw ViewCreateOperation.Failed(name, "reversed", result.Reversed ? "reversed" : "not reversed", section.Reversed ? "reversed" : "not reversed");
        var orientation = request.Cut.Orientation;
        bool sharesX = ViewCutGeometry.SharesX(orientation);
        double along = sharesX ? facts.Position.Y - _at.Y : facts.Position.X - _at.X;
        if (Math.Abs(along) > ViewGeometry.PositionToleranceMm) throw ViewCreateOperation.Failed(name, "position", $"{(sharesX ? "y" : "x")} of {_at}", facts.Position.ToString());
        var parent = drawing.ReadView(_parent)!;
        if (!DrawingReader.SameScale(facts.Scale, parent.Scale)) throw ViewCreateOperation.Failed(name, "scale", $"{_parent}'s, {parent.Scale}", facts.Scale.ToString());
        var parentT = drawing.Transform(_parent)!;
        var t = ViewChecks.Scale(drawing, name, parentT.Scale);
        if (ViewCutGeometry.SectionAlignmentMismatch(parentT, t, orientation) is string why)
            throw ViewCreateOperation.Failed(name, "alignment", $"aligned with {_parent}, cut {WireNames.ForEnum(orientation)}", why);
        var faces = drawing.CutFaces(name);
        if (ViewCutGeometry.CutPlaneMismatch(ViewGeometry.Facing(t), result.Through, faces) is string plane)
            throw ViewCreateOperation.Failed(name, "cut_plane", $"hatched faces in the plane through {result.Through} square to {ViewGeometry.Facing(t)}", plane);
        var drawn = DrawingReader.Extents(drawing, facts) ?? throw ViewCreateOperation.Failed(name, "extents", "edges drawn", "nothing drawn");
        if (drawing.ModelOf(name)?.BoundingBox(precise: true) is Box3 box && !ViewGeometry.Within(drawn, ViewGeometry.Project(t, box)))
            throw ViewCreateOperation.Failed(name, "extents", $"within {ViewGeometry.Project(t, box)}", drawn.ToString());
        result.HatchedFaces = faces.Count;
        result.HatchedAreaMm2 = faces.Sum(f => f.AreaMm2);
        result.View = Describe(doc, drawing, name);
    }
}

public sealed class ViewDetailOperation : DrawingChangeOperation<ViewDetailRequest, ViewDetailResult>
{
    /// <summary>The gap between a parent's drawn edges and a detail placed beside it, sheet mm.</summary>
    public const double GapMm = 20;

    public ViewDetailOperation(AddInServices services)
        : base(services)
    {
    }

    string _parent = "";
    SheetPoint _at = new SheetPoint();

    protected override string? DocumentOf(ViewDetailRequest request) => request.Document;

    protected override ViewDetailResult Change(ISwDocument doc, ISwDrawing drawing, ViewDetailRequest request, ModelTransaction tx, IOperationContext context)
    {
        _parent = View(doc, drawing, request.Parent, "/parent");
        var (parent, model, t) = ViewCuts.Parent(drawing, _parent, "detail", sections: true);
        var centre = ModelPlaces.Resolve(Services.References, model, request.Centre ?? new ModelPlace(), "/centre");
        if (!(request.RadiusMm > 0) || double.IsInfinity(request.RadiusMm))
            throw ViewRequests.Invalid("/radius_mm", FormattableString.Invariant($"/radius_mm: the detail circle's radius must be more than 0 model mm, not {request.RadiusMm}"));
        if (request.Scale == null) throw ViewRequests.Invalid("/scale", "/scale: give the scale the detail is drawn at (2:1 is twice the model's size)");
        ViewRequests.Scale(request.Scale, "/scale");
        var extents = DrawingReader.Extents(drawing, parent) ?? throw ViewRequests.Invalid("/parent", $"/parent: {_parent} draws nothing to detail");
        var drawnCentre = ViewGeometry.ToSheet(t, centre.Point);
        if (!ViewGeometry.Within(new SheetBox { Min = drawnCentre, Max = drawnCentre }, extents))
            throw ViewRequests.Invalid("/centre", $"/centre: {centre.Described} is drawn at {drawnCentre} in {_parent}, outside what it draws ({extents}), so the detail would show nothing of it");
        string label = ViewCuts.Label(drawing, request.Label);
        var sheet = drawing.ReadSheet(parent.Sheet)!;
        double scale = DrawingReader.Ratio(request.Scale);
        _at = request.At ?? ViewCutGeometry.DetailPlace(extents, request.RadiusMm * scale, GapMm);
        ViewRequests.OnSheet(_at, sheet, "/at");

        ViewCuts.Show(drawing, parent.Sheet);
        string name = drawing.DetailView(_parent, drawnCentre, request.RadiusMm * t.Scale, _at, request.Scale, label)
            ?? throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS made no detail of {_parent} around {centre.Described}; nothing was left on the drawing",
                new JsonObject { ["parent"] = _parent, ["label"] = label });
        MadeCut(doc, drawing, tx, name, _parent, label, section: false);
        context.Log.Info(FormattableString.Invariant($"view.detail: {name} ({label}) of {_parent} around {centre.Described}, r {request.RadiusMm} mm, at {request.Scale} on {_at}"));
        return new ViewDetailResult
        {
            View = new ViewInfo { Name = name, Sheet = parent.Sheet },
            Label = label,
            Centre = centre.Point,
            CentreIs = centre.Described,
            RadiusMm = request.RadiusMm,
            ModelUnsaved = model.Info().Dirty,
        };
    }

    /// <summary>
    /// Design 611, Sections and details: a detail of the parent with the label asked, where asked, at its scale, turned as
    /// its parent, drawing edges (not empty) that all lie inside the circle's image at that scale.
    /// </summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, ViewDetailRequest request, ViewDetailResult result)
    {
        string name = result.View.Name;
        var facts = drawing.ReadView(name) ?? throw ViewCreateOperation.Failed(name, "exists", "the view on its sheet", "no such view");
        if (facts.Type != ViewType.Detail) throw ViewCreateOperation.Failed(name, "type", "detail", WireNames.ForEnum(facts.Type));
        if (facts.Parent != _parent) throw ViewCreateOperation.Failed(name, "parent", _parent, facts.Parent ?? "none");
        var detail = drawing.ReadDetail(name) ?? throw ViewCreateOperation.Failed(name, "detail", $"the detail {result.Label}", "no detail");
        if (!string.Equals(detail.Label, result.Label, StringComparison.OrdinalIgnoreCase)) throw ViewCreateOperation.Failed(name, "label", result.Label, detail.Label);
        if (!ViewGeometry.Near(facts.Position, _at)) throw ViewCreateOperation.Failed(name, "position", _at.ToString(), facts.Position.ToString());
        if (!DrawingReader.SameScale(facts.Scale, request.Scale)) throw ViewCreateOperation.Failed(name, "scale", request.Scale.ToString(), facts.Scale.ToString());
        double scale = DrawingReader.Ratio(request.Scale);
        var t = ViewChecks.Scale(drawing, name, scale);
        if (ViewCutGeometry.DetailTurnMismatch(drawing.Transform(_parent)!, t) is string turned)
            throw ViewCreateOperation.Failed(name, "turn", $"turned as {_parent}", turned);
        var points = DrawingReader.Points(drawing, facts);
        if (points.Count == 0)
        {
            throw ViewCreateOperation.Failed(name, "empty", "edges inside the detail circle",
                "nothing drawn: SOLIDWORKS made an empty detail, as CADapter saw it do; this is a known gap, so the detail was not kept");
        }
        var centre = ViewGeometry.ToSheet(t, result.Centre);
        double radius = result.RadiusMm * scale;
        if (ViewCutGeometry.OutsideCircle(points, centre, radius) is string outside)
            throw ViewCreateOperation.Failed(name, "drawn", FormattableString.Invariant($"every drawn point within {radius:0.###} mm of {centre}"), outside);
        result.PointsInside = points.Count;
        result.View = Describe(doc, drawing, name);
    }
}
