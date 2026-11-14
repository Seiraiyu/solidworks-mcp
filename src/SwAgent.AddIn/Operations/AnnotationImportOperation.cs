using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationImportOperation : DrawingChangeOperation<AnnotationImportRequest, AnnotationImportResult>
{
    public AnnotationImportOperation(AddInServices services)
        : base(services)
    {
    }

    List<AnnotationFacts> _made = new List<AnnotationFacts>();

    protected override string? DocumentOf(AnnotationImportRequest request) => request.Document;

    protected override AnnotationImportResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationImportRequest request, ModelTransaction tx, IOperationContext context)
    {
        var types = request.Types == null || request.Types.Count == 0 ? new List<AnnotationImportType> { AnnotationImportType.Dimensions } : request.Types.Distinct().ToList();
        List<string>? views = null;
        if (request.Views != null)
        {
            if (request.Views.Count == 0) throw ViewRequests.Invalid("/views", "/views: name at least one view, or leave views out for every view");
            views = new List<string>();
            for (int i = 0; i < request.Views.Count; i++)
            {
                string view = View(doc, drawing, request.Views[i], $"/views/{i}");
                if (views.Contains(view)) throw ViewRequests.Invalid($"/views/{i}", $"/views/{i}: {view} is named twice");
                views.Add(view);
            }
        }
        if (DrawingReader.ViewCount(drawing) == 0)
            throw ViewRequests.Invalid("/document", $"{doc.Info().Title} has no views to bring the models' annotations into; make one with view.create first");

        var before = DrawingAnnotations.All(drawing);
        drawing.ImportAnnotations(types);
        var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
        int removed = 0;
        if (views != null)
        {
            // SOLIDWORKS brings them into every view (the reliable path); what went into views not asked goes again.
            var others = made.Where(a => a.View == null || !views.Contains(a.View)).ToList();
            foreach (var owner in others.GroupBy(a => (a.Sheet, a.View)))
                drawing.DeleteAnnotations(owner.Key.Sheet, owner.Key.View, owner.Select(a => a.Name).ToList());
            var left = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
            removed = others.Count - left.Count(a => a.View == null || !views.Contains(a.View));
            made = left;
        }
        foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
        var stray = views == null ? new List<AnnotationFacts>() : made.Where(a => a.View == null || !views.Contains(a.View)).ToList();
        if (stray.Count > 0)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not delete what it brought into views not asked for: {string.Join(", ", stray.Select(a => $"{a.Name} in {DrawingAnnotations.Where(a)}"))}",
                new JsonObject { ["left"] = stray.Select(a => (object?)a.Name).ToList() });
        }
        _made = made;
        context.Log.Info($"annotation.import: {string.Join(", ", types.Select(t => WireNames.ForEnum(t)))} into {(views == null ? "every view" : string.Join(", ", views))}: {made.Count} brought, {removed} removed from other views");
        return new AnnotationImportResult { Removed = removed };
    }

    /// <summary>
    /// Design 611: what each view gained, counted on the sheet; each imported dimension is the model's own, its value on the
    /// drawing its value in the model.
    /// </summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationImportRequest request, AnnotationImportResult result)
    {
        var owners = DrawingAnnotations.Owners(drawing);
        var models = new Dictionary<string, IReadOnlyList<DimensionRecord>>(StringComparer.Ordinal);
        var views = new ShownEntities(Services.References, drawing);
        foreach (var made in _made)
        {
            var a = Annotating.Reread(drawing, made);
            var info = DrawingAnnotations.Info(Services.References, doc, drawing, a, owners, views);
            result.Annotations.Add(info);
            if (a.Kind != AnnotationKind.Dimension) continue;
            if (a.DimensionName == null || info.ModelDimension != true)
                throw Annotating.Failed(a.Name, "model", "one of the model's dimensions", a.DimensionName == null ? "a dimension SOLIDWORKS gives no name for" : $"{a.DimensionName}, the drawing's own");
            string name = info.DimensionName!;
            var model = drawing.ModelOf(a.View!) ?? throw Annotating.Failed(a.Name, "model", $"{a.View}'s model loaded", "no model");
            if (!models.TryGetValue(a.View!, out var records)) models[a.View!] = records = model.Dimensions(null);
            var record = records.FirstOrDefault(r => string.Equals(r.FullName, name, StringComparison.OrdinalIgnoreCase))
                ?? throw Annotating.Failed(a.Name, "in_model", $"{name} in {model.Info().Title}", "no such dimension in the model");
            double tolerance = record.Angle ? AnnotationGeometry.AngleToleranceDeg : AnnotationGeometry.ValueToleranceMm;
            if (a.Value == null || Math.Abs(a.Value.Value - record.Value) > tolerance)
                throw Annotating.Failed(a.Name, "value", FormattableString.Invariant($"{name} = {record.Value:0.######} (the model's)"), a.Value == null ? "no value" : FormattableString.Invariant($"{a.Value.Value:0.######}"));
            result.Dimensions.Add(new ImportedDimension { Annotation = a.Name, Reference = info.Reference, View = a.View!, Dimension = name, Value = a.Value.Value, ModelValue = record.Value, Angle = record.Angle });
        }
        foreach (var shown in result.Dimensions.GroupBy(d => d.Dimension, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(d => d.View).Distinct().Count() > 1))
            result.Duplicates.Add($"{shown.Key} in {string.Join(", ", shown.Select(d => d.View).Distinct())}");
    }
}
