using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationListOperation : Operation<AnnotationListRequest, AnnotationListResult>
{
    readonly AddInServices _services;

    public AnnotationListOperation(AddInServices services)
    {
        _services = services;
    }

    protected override AnnotationListResult Run(AnnotationListRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var all = Listed(_services.References, doc, drawing, request);
        var owners = DrawingAnnotations.Owners(drawing);
        var shown = new ShownEntities(_services.References, drawing);
        var result = new AnnotationListResult { Annotations = all.Select(a => DrawingAnnotations.Info(_services.References, doc, drawing, a, owners, shown)).ToList() };
        context.Log.Info($"annotation.list: {result.Annotations.Count} annotations");
        return result;
    }

    /// <summary>The annotations asked for: every one, a sheet's (its own and its views'), or a view's.</summary>
    static List<AnnotationFacts> Listed(ReferenceService references, ISwDocument doc, ISwDrawing drawing, AnnotationListRequest request)
    {
        string? sheet = request.Sheet == null ? null : references.ResolveSheet(doc, drawing, request.Sheet, "/sheet");
        string? view = request.View == null ? null : references.ResolveView(doc, drawing, request.View, "/view");
        if (sheet != null && view != null && drawing.ReadView(view)!.Sheet != sheet)
            throw ViewRequests.Invalid("/view", $"/view: {view} is on {drawing.ReadView(view)!.Sheet}, not {sheet}");
        var all = DrawingAnnotations.All(drawing);
        return all.Where(a => (sheet == null || a.Sheet == sheet) && (view == null || a.View == view)).ToList();
    }

    /// <summary>The drawing still has as many annotations there: nothing changed it while it was read.</summary>
    protected override void Verify(AnnotationListRequest request, AnnotationListResult result, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        int now = Listed(_services.References, doc, drawing, request).Count;
        if (now != result.Annotations.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the drawing's annotations changed while they were read ({result.Annotations.Count}, now {now}); ask again",
                new JsonObject { ["check"] = "unchanged" });
        }
    }
}

public sealed class AnnotationSetOperation : DrawingChangeOperation<AnnotationSetRequest, AnnotationResult>
{
    public AnnotationSetOperation(AddInServices services)
        : base(services)
    {
    }

    AnnotationFacts _old = new AnnotationFacts();

    protected override string? DocumentOf(AnnotationSetRequest request) => request.Document;

    protected override AnnotationResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationSetRequest request, ModelTransaction tx, IOperationContext context)
    {
        var a = Annotation(doc, drawing, request.Annotation, "/annotation");
        _old = a;
        if (request.At == null && request.Text == null) throw ViewRequests.Invalid("/annotation", "annotation.set changes nothing: give at or text");
        if (request.Text != null)
        {
            if (a.Kind != AnnotationKind.Note)
                throw ViewRequests.Invalid("/text", $"/text: {a.Name} is a {WireNames.ForEnum(a.Kind).Replace('_', ' ')}; only a note's text is set here (a model dimension's value is dimension.set on the model)");
            if (string.IsNullOrWhiteSpace(request.Text)) throw ViewRequests.Invalid("/text", "/text: a note needs text");
        }
        if (request.At != null)
        {
            // The 5.C live run (round 8): SP4 leaves a centre mark on its circle's centre whatever IAnnotation.SetPosition2
            // is given (written for (125, 185), it still read (120, 180)), so moving one is refused before SOLIDWORKS is asked.
            if (a.Kind == AnnotationKind.CentreMark)
                throw ViewRequests.Invalid("/at", $"/at: {a.Name} is a centre mark, which sits on its circle's centre and cannot be moved (SOLIDWORKS keeps it there)");
            ViewRequests.OnSheet(request.At, drawing.ReadSheet(a.Sheet)!, "/at");
            drawing.MoveAnnotation(a.Sheet, a.View, a.Name, request.At);
            tx.Edited(new ChangedItem { Name = a.Name, Property = "position", Before = a.Position?.ToString() ?? "", After = request.At.ToString() }, () =>
            {
                if (a.Position == null) return;
                drawing.MoveAnnotation(a.Sheet, a.View, a.Name, a.Position);
                var now = DrawingAnnotations.Read(drawing, a.Sheet, a.View, a.Name);
                if (a.Position != null && (now?.Position == null || !ViewGeometry.Near(now.Position, a.Position)))
                    throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not put {a.Name} back at {a.Position} (it reads {now?.Position?.ToString() ?? "nowhere"})");
            });
        }
        if (request.Text != null)
        {
            string old = string.IsNullOrEmpty(a.LinkedText) ? a.Text ?? "" : a.LinkedText!;
            if (!drawing.SetNoteText(a.Sheet, a.View, a.Name, request.Text))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not give {a.Name} the text '{request.Text}'", new JsonObject { ["annotation"] = a.Name });
            tx.Edited(new ChangedItem { Name = a.Name, Property = "text", Before = old, After = request.Text }, () =>
            {
                if (!drawing.SetNoteText(a.Sheet, a.View, a.Name, old)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not give {a.Name} its old text back");
            });
        }
        context.Log.Info($"annotation.set: {a.Name} in {DrawingAnnotations.Where(a)}: {string.Join(", ", tx.Changed.Select(c => $"{c.Property} {c.Before} -> {c.After}"))}");
        return new AnnotationResult();
    }

    /// <summary>Every value set reads back: the position within 0.01 mm, the text as written.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationSetRequest request, AnnotationResult result)
    {
        var a = Annotating.Reread(drawing, _old);
        if (request.At != null && (a.Position == null || !ViewGeometry.Near(a.Position, request.At)))
            throw Annotating.Failed(a.Name, "position", request.At.ToString(), a.Position?.ToString() ?? "none");
        string written = string.IsNullOrEmpty(a.LinkedText) ? a.Text ?? "" : a.LinkedText!;
        if (request.Text != null && !string.Equals(written, request.Text, StringComparison.Ordinal))
            throw Annotating.Failed(a.Name, "text", request.Text, written);
        result.Annotation = DescribeAnnotation(doc, drawing, a);
    }
}

public sealed class AnnotationDeleteOperation : DrawingChangeOperation<AnnotationDeleteRequest, AnnotationDeleteResult>
{
    public AnnotationDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    List<AnnotationFacts> _deleted = new List<AnnotationFacts>();
    List<string> _kept = new List<string>();
    List<(ISwDocument Model, string Name, double Value, bool Angle)> _models = new List<(ISwDocument, string, double, bool)>();

    protected override string? DocumentOf(AnnotationDeleteRequest request) => request.Document;

    static string Named(AnnotationFacts a) => a.Name + "@" + (a.View ?? a.Sheet);

    protected override AnnotationDeleteResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Annotations.Count == 0) throw ViewRequests.Invalid("/annotations", "/annotations: name at least one annotation to delete");
        _deleted = new List<AnnotationFacts>();
        for (int i = 0; i < request.Annotations.Count; i++)
        {
            var a = Annotation(doc, drawing, request.Annotations[i], $"/annotations/{i}");
            if (_deleted.Any(d => DrawingAnnotations.Key(d) == DrawingAnnotations.Key(a))) throw ViewRequests.Invalid($"/annotations/{i}", $"/annotations/{i}: {Named(a)} is named twice");
            _deleted.Add(a);
        }
        var keys = new HashSet<string>(_deleted.Select(DrawingAnnotations.Key), StringComparer.Ordinal);
        _kept = DrawingAnnotations.All(drawing).Select(DrawingAnnotations.Key).Where(k => !keys.Contains(k)).ToList();
        // An imported model dimension leaves the drawing only: the model must keep it, with its value.
        var owners = DrawingAnnotations.Owners(drawing);
        _models = _deleted.Where(a => a.Kind == AnnotationKind.Dimension && a.View != null && a.DimensionName != null && a.Value != null && AnnotationGeometry.IsModelDimension(a.DimensionName, owners))
            .Select(a => (drawing.ModelOf(a.View!), SolidWorksCodes.DimensionName(a.DimensionName!), a.Value!.Value, a.DimensionKind == DimensionKind.Angle))
            .Where(m => m.Item1 != null).Select(m => (m.Item1!, m.Item2, m.Item3, m.Item4)).ToList();

        foreach (var owner in _deleted.GroupBy(a => (a.Sheet, a.View)))
        {
            var names = owner.Select(a => a.Name).ToList();
            if (!drawing.DeleteAnnotations(owner.Key.Sheet, owner.Key.View, names))
            {
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", owner.Select(Named))}",
                    new JsonObject { ["annotations"] = owner.Select(a => (object?)Named(a)).ToList() });
            }
            // A deleted annotation cannot be made again as it was: a rollback after this says so (rollback_failed) for each that is gone.
            foreach (var a in owner)
            {
                tx.Edited(new ChangedItem { Name = Named(a), Property = "deleted", Before = WireNames.ForEnum(a.Kind), After = "deleted" }, () =>
                {
                    if (DrawingAnnotations.Read(drawing, a.Sheet, a.View, a.Name) == null)
                        throw new SwAgentException(ErrorCode.OperationFailed, $"{Named(a)} was deleted and cannot be put back");
                });
            }
        }
        context.Log.Info($"annotation.delete: {string.Join(", ", _deleted.Select(Named))}");
        return new AnnotationDeleteResult { Deleted = _deleted.Select(Named).ToList() };
    }

    /// <summary>Each annotation deleted is gone, every other is still there, and the model keeps each model dimension deleted, with its value.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationDeleteRequest request, AnnotationDeleteResult result)
    {
        var now = new HashSet<string>(DrawingAnnotations.All(drawing).Select(DrawingAnnotations.Key), StringComparer.Ordinal);
        var left = _deleted.Where(a => now.Contains(DrawingAnnotations.Key(a))).Select(Named).ToList();
        var lost = _kept.Where(k => !now.Contains(k)).Select(k => string.Join("@", k.Split('\n').Reverse().Where(p => p.Length > 0).Take(2))).ToList();
        if (left.Count > 0 || lost.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                left.Count > 0 ? $"SOLIDWORKS reported the annotations deleted, but {string.Join(", ", left)} {(left.Count == 1 ? "is" : "are")} still there" : $"deleting went too far: {string.Join(", ", lost)} went too",
                new JsonObject { ["check"] = "annotations", ["left"] = left.Select(l => (object?)l).ToList(), ["lost"] = lost.Select(l => (object?)l).ToList() });
        }
        foreach (var (model, name, value, angle) in _models)
        {
            var kept = model.Dimensions(null).FirstOrDefault(d => string.Equals(d.FullName, name, StringComparison.OrdinalIgnoreCase));
            double tolerance = angle ? AnnotationGeometry.AngleToleranceDeg : AnnotationGeometry.ValueToleranceMm;
            if (kept == null || Math.Abs(kept.Value - value) > tolerance)
                throw Annotating.Failed(name, "model_kept", FormattableString.Invariant($"{name} = {value:0.######} still in {model.Info().Title}"), kept == null ? "gone from the model" : FormattableString.Invariant($"{kept.Value:0.######}"));
            result.ModelDimensionsKept.Add(FormattableString.Invariant($"{name} = {value:0.######}"));
        }
    }
}
