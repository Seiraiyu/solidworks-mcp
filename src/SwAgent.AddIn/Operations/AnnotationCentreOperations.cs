using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationCentreMarksOperation : DrawingChangeOperation<AnnotationCentreMarksRequest, AnnotationCentresResult>
{
    public AnnotationCentreMarksOperation(AddInServices services)
        : base(services)
    {
    }

    readonly List<(AnnotationFacts Mark, SheetPoint Centre)> _made = new List<(AnnotationFacts, SheetPoint)>();
    string _view = "";
    int _before;

    protected override string? DocumentOf(AnnotationCentreMarksRequest request) => request.Document;

    protected override AnnotationCentresResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationCentreMarksRequest request, ModelTransaction tx, IOperationContext context)
    {
        _made.Clear();
        _view = View(doc, drawing, request.View, "/view");
        var (facts, model, t) = Annotating.Showing(drawing, _view);
        var circles = new List<(object Entity, ViewEntityInfo Drawn)>();
        if (request.Entities != null)
        {
            if (request.Entities.Count == 0) throw ViewRequests.Invalid("/entities", "/entities: name at least one circular edge, or leave entities out for every circle the view shows");
            var named = Annotating.InView(Services.References, doc, drawing, _view, request.Entities, ReferenceKind.Edge);
            for (int i = 0; i < named.Count; i++)
            {
                var drawn = ViewGeometry.Draw(t, Services.References.Describe(model, named[i].Entity), false);
                if (drawn.Shape != DrawnShape.Circle && drawn.Shape != DrawnShape.Arc)
                    throw ViewRequests.Invalid($"/entities/{i}", $"/entities/{i}: {_view} draws that edge as {WireNames.ForEnum(drawn.Shape)}, not as a circle or arc, so it takes no centre mark");
                circles.Add((named[i].Entity, drawn));
            }
        }
        else
        {
            if (model.Info().Type != DocumentType.Part) throw ViewRequests.Invalid("/entities", $"/entities: {_view} shows an assembly; name the circular edges to mark");
            circles.AddRange(drawing.VisibleEntities(_view, edges: true, vertices: false).Select(e => (e.Entity, ViewGeometry.Draw(t, Services.References.Describe(model, e.Entity), false))));
        }
        var targets = AnnotationGeometry.DistinctCentres(circles, c => c.Drawn);
        var marks = drawing.Annotations(facts.Sheet, _view).Where(a => a.Kind == AnnotationKind.CentreMark).ToList();
        _before = marks.Count;
        var todo = targets.Where(c => !marks.Any(m => m.Position != null && ViewGeometry.Near(m.Position, c.Drawn.Centre!))).ToList();
        foreach (var (entity, drawn) in todo)
        {
            var before = DrawingAnnotations.All(drawing);
            string? name = drawing.AddCentreMark(_view, entity);
            var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
            foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
            _made.Add((Annotating.Mine(made, name, $"centre mark on the circle {_view} draws at {drawn.Centre}"), drawn.Centre!));
        }
        context.Log.Info($"annotation.centre_marks: {_view}: {targets.Count} circles, {targets.Count - todo.Count} already marked, {_made.Count} marked");
        return new AnnotationCentresResult { Eligible = targets.Count, Already = targets.Count - todo.Count };
    }

    /// <summary>Design 611: the view gained one centre mark per circle, counted by walking its annotations, each at its circle's drawn centre.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationCentreMarksRequest request, AnnotationCentresResult result)
    {
        foreach (var (mark, centre) in _made)
        {
            var a = Annotating.Reread(drawing, mark);
            if (a.Kind != AnnotationKind.CentreMark) throw Annotating.Failed(a.Name, "kind", "a centre mark", WireNames.ForEnum(a.Kind));
            if (a.Position == null || !ViewGeometry.Near(a.Position, centre)) throw Annotating.Failed(a.Name, "position", $"the circle's centre {centre}", (a.Position?.ToString() ?? "none") + (a.PositionNote == null ? "" : $" ({a.PositionNote})"));
            result.Made.Add(DescribeAnnotation(doc, drawing, a));
        }
        string sheet = drawing.ReadView(_view)!.Sheet;
        int now = drawing.Annotations(sheet, _view).Count(a => a.Kind == AnnotationKind.CentreMark);
        if (now != _before + _made.Count) throw Annotating.Failed(_view, "count", $"{_before + _made.Count} centre marks in {_view} ({_before} before, {_made.Count} made)", now.ToString());
    }
}

public sealed class AnnotationCentreLinesOperation : DrawingChangeOperation<AnnotationCentreLinesRequest, AnnotationCentresResult>
{
    public AnnotationCentreLinesOperation(AddInServices services)
        : base(services)
    {
    }

    readonly List<(AnnotationFacts Line, IReadOnlyList<object> On)> _made = new List<(AnnotationFacts, IReadOnlyList<object>)>();
    string _view = "";
    int _before;

    protected override string? DocumentOf(AnnotationCentreLinesRequest request) => request.Document;

    protected override AnnotationCentresResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationCentreLinesRequest request, ModelTransaction tx, IOperationContext context)
    {
        _made.Clear();
        _view = View(doc, drawing, request.View, "/view");
        var (facts, model, t) = Annotating.Showing(drawing, _view);
        var lines = drawing.Annotations(facts.Sheet, _view).Where(a => a.Kind == AnnotationKind.CentreLine).ToList();
        _before = lines.Count;
        // Each target: what the centre line is made on, and for a cylinder its axis.
        var targets = new List<(IReadOnlyList<object> On, (Point3 Origin, Vector3 Axis)? Axis)>();
        if (request.Entities != null)
        {
            if (request.Entities.Count == 0) throw ViewRequests.Invalid("/entities", "/entities: name cylindrical faces or two straight edges, or leave entities out for every cylinder the view shows from the side");
            var named = Annotating.InView(Services.References, doc, drawing, _view, request.Entities, ReferenceKind.Face, ReferenceKind.Edge);
            if (named.Any(e => e.Kind == ReferenceKind.Edge))
            {
                var drawn = named.Select(e => ViewGeometry.Draw(t, Services.References.Describe(model, e.Entity), false)).ToList();
                if (named.Count != 2 || named.Any(e => e.Kind != ReferenceKind.Edge) || !AnnotationGeometry.Parallel(drawn[0], drawn[1]))
                    throw ViewRequests.Invalid("/entities", "/entities: a centre line between edges takes exactly two straight edges the view draws parallel, apart (or name cylindrical faces alone)");
                targets.Add((named.Select(e => e.Entity).ToList(), null));
            }
            else
            {
                for (int i = 0; i < named.Count; i++)
                {
                    var face = Services.References.Describe(model, named[i].Entity);
                    if (face.Geometry != GeometryType.Cylinder || face.Origin == null || face.Axis == null || !AnnotationGeometry.SeenFromSide(t, face.Axis))
                        throw ViewRequests.Invalid($"/entities/{i}", $"/entities/{i}: a centre line runs along a cylindrical face the view sees from the side; {_view} sees this {WireNames.ForEnum(face.Geometry)} face otherwise");
                    targets.Add((new[] { named[i].Entity }, (face.Origin, face.Axis)));
                }
            }
        }
        else
        {
            if (model.Info().Type != DocumentType.Part) throw ViewRequests.Invalid("/entities", $"/entities: {_view} shows an assembly; name the faces or edges");
            var cylinders = drawing.VisibleFaces(_view).Select(f => (Face: f, Info: Services.References.Describe(model, f)))
                .Where(f => f.Info.Geometry == GeometryType.Cylinder && f.Info.Origin != null && f.Info.Axis != null).ToList();
            foreach (var c in AnnotationGeometry.DistinctAxes(t, cylinders, c => (c.Info.Origin!, c.Info.Axis!)))
                targets.Add((new[] { c.Face }, (c.Info.Origin!, c.Info.Axis!)));
        }
        var todo = targets.Where(target => !Taken(drawing, model, t, lines, target)).ToList();
        foreach (var (on, _) in todo)
        {
            var before = DrawingAnnotations.All(drawing);
            string? name = drawing.AddCentreLine(_view, on);
            var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
            foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
            _made.Add((Annotating.Mine(made, name, $"centre line in {_view}"), on));
        }
        context.Log.Info($"annotation.centre_lines: {_view}: {targets.Count} targets, {targets.Count - todo.Count} already had one, {_made.Count} made");
        return new AnnotationCentresResult { Eligible = targets.Count, Already = targets.Count - todo.Count };
    }

    /// <summary>
    /// True when a centre line is already on the target: attached to every entity it would be made on, or, for a cylinder,
    /// to a face on the same drawn axis.
    /// </summary>
    bool Taken(ISwDrawing drawing, ISwDocument model, ViewTransform t, IEnumerable<AnnotationFacts> lines, (IReadOnlyList<object> On, (Point3 Origin, Vector3 Axis)? Axis) target)
    {
        foreach (var line in lines)
        {
            if (target.On.All(o => line.Attached.Any(a => drawing.SameEntity(a, o)))) return true;
            if (!(target.Axis is { } axis)) continue;
            foreach (var attached in line.Attached)
            {
                if (model.KindOf(attached) != ReferenceKind.Face) continue;
                var face = Services.References.Describe(model, attached);
                if (face.Geometry != GeometryType.Cylinder || face.Origin == null || face.Axis == null) continue;
                var pair = new[] { (face.Origin, face.Axis), (axis.Origin, axis.Axis) };
                if (AnnotationGeometry.DistinctAxes(t, pair, p => (p.Item1, p.Item2)).Count == 1) return true;
            }
        }
        return false;
    }

    /// <summary>Design 611: the view gained one centre line per target, counted by walking its annotations, each attached to what it was made on.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationCentreLinesRequest request, AnnotationCentresResult result)
    {
        foreach (var (line, on) in _made)
        {
            var a = Annotating.Reread(drawing, line);
            if (a.Kind != AnnotationKind.CentreLine) throw Annotating.Failed(a.Name, "kind", "a centre line", WireNames.ForEnum(a.Kind));
            if (!on.All(o => a.Attached.Any(x => drawing.SameEntity(x, o))))
                throw Annotating.Failed(a.Name, "attached", on.Count == 1 ? "attached to the face it was made on" : "attached to both edges it was made between", $"attached to {a.Attached.Count} entities, not all of them");
            result.Made.Add(DescribeAnnotation(doc, drawing, a));
        }
        string sheet = drawing.ReadView(_view)!.Sheet;
        int now = drawing.Annotations(sheet, _view).Count(a => a.Kind == AnnotationKind.CentreLine);
        if (now != _before + _made.Count) throw Annotating.Failed(_view, "count", $"{_before + _made.Count} centre lines in {_view} ({_before} before, {_made.Count} made)", now.ToString());
    }
}
