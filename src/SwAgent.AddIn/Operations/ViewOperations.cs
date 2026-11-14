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
