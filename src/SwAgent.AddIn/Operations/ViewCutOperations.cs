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
    /// <summary>The parent named, which must be a model or projected view showing a loaded model; with its facts, model and transform.</summary>
    public static (ViewFacts Facts, ISwDocument Model, ViewTransform Transform) Parent(ISwDrawing drawing, string parent, string what)
    {
        var facts = drawing.ReadView(parent)!;
        if (facts.Type != ViewType.Model && facts.Type != ViewType.Projected)
            throw ViewRequests.Invalid("/parent", $"/parent: {parent} is a {WireNames.ForEnum(facts.Type)} view; {what} a model view or a projected view");
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
