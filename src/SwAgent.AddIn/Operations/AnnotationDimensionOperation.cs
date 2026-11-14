using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationDimensionOperation : DrawingChangeOperation<AnnotationDimensionRequest, AnnotationDimensionResult>
{
    public AnnotationDimensionOperation(AddInServices services)
        : base(services)
    {
    }

    DimensionExpectation _expected = new DimensionExpectation(0, null, "");
    AnnotationFacts _made = new AnnotationFacts();
    string _view = "";

    protected override string? DocumentOf(AnnotationDimensionRequest request) => request.Document;

    protected override AnnotationDimensionResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationDimensionRequest request, ModelTransaction tx, IOperationContext context)
    {
        var given = request.Entities ?? new List<ViewEntity>();
        if (given.Count < 1 || given.Count > 2)
            throw ViewRequests.Invalid("/entities", $"/entities: a dimension takes one or two entities of one view, not {given.Count}");
        var entities = new List<ResolvedViewEntity>();
        for (int i = 0; i < given.Count; i++)
        {
            var e = ViewEntities.ResolveShown(Services.References, doc, drawing, given[i], $"/entities/{i}", ReferenceKind.Edge, ReferenceKind.Vertex);
            if (i > 0 && e.View != entities[0].View)
                throw ViewRequests.Invalid($"/entities/{i}/view", $"/entities/{i}/view: a dimension's entities are in one view; this one is in {e.View}, the first in {entities[0].View}");
            entities.Add(e);
        }
        _view = entities[0].View;
        var view = drawing.ReadView(_view)!;
        ViewRequests.OnSheet(request.At, drawing.ReadSheet(view.Sheet)!, "/at");
        var t = drawing.Transform(_view) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no transform for {_view}");
        var drawn = entities.Select(e => ViewGeometry.Draw(t, Services.References.Describe(e.Model, e.Own), e.Kind == ReferenceKind.Vertex)).ToList();
        _expected = AnnotationGeometry.Expect(request.Kind, drawn, t.Scale, out string why)
            ?? throw ViewRequests.Invalid("/entities", $"/entities: {why}");

        var before = DrawingAnnotations.All(drawing);
        string? name = drawing.AddDimension(_view, request.Kind, entities.Select(e => e.Entity).ToList(), request.At);
        var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
        foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
        _made = Annotating.Mine(made, name, $"{WireNames.ForEnum(request.Kind)} dimension in {_view}");
        context.Log.Info(FormattableString.Invariant($"annotation.dimension: {_made.Name} in {_view}, {WireNames.ForEnum(request.Kind)}, {_expected.Quantity} {_expected.Value:0.######} on the model"));
        return new AnnotationDimensionResult { ModelValue = _expected.Value, Measured = _expected.Quantity };
    }

    /// <summary>Design 611: a driven dimension of the drawing in the view, whose value is the model's between its entities.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationDimensionRequest request, AnnotationDimensionResult result)
    {
        var a = Annotating.Reread(drawing, _made);
        if (a.Kind != AnnotationKind.Dimension) throw Annotating.Failed(a.Name, "kind", "a dimension", WireNames.ForEnum(a.Kind));
        if (a.DimensionKind is DimensionKind read && !Compatible(read, request.Kind))
            throw Annotating.Failed(a.Name, "kind", WireNames.ForEnum(request.Kind), WireNames.ForEnum(read) + (a.DimensionNote == null ? "" : $" ({a.DimensionNote})"));
        bool angle = request.Kind == DimensionKind.Angle;
        if (a.Value == null || !AnnotationGeometry.Matches(_expected, a.Value.Value, angle))
        {
            string expected = FormattableString.Invariant($"{_expected.Value:0.######}{(_expected.Alternative is double other ? $" or {other:0.######}" : "")}{(angle ? "°" : " mm")} ({_expected.Quantity}, on the model)");
            throw Annotating.Failed(a.Name, "value", expected, (a.Value == null ? "no value" : FormattableString.Invariant($"{a.Value.Value:0.######}")) + (a.DimensionNote == null ? "" : $" ({a.DimensionNote})"));
        }
        if (!a.Driven) throw Annotating.Failed(a.Name, "driven", "a reference (driven) dimension", "a driving dimension");
        var info = DescribeAnnotation(doc, drawing, a);
        if (info.ModelDimension == true) throw Annotating.Failed(a.Name, "reference", "a dimension of the drawing", $"{info.DimensionName}, one of the model's");
        result.Annotation = info;
        result.Value = a.Value.Value;
        if (_expected.Alternative is double supplement && Math.Abs(a.Value.Value - supplement) < Math.Abs(a.Value.Value - _expected.Value)) result.ModelValue = supplement;
    }

    /// <summary>SOLIDWORKS may read a horizontal or vertical dimension back as linear, and the other way round; the value tells them apart.</summary>
    static bool Compatible(DimensionKind read, DimensionKind asked)
    {
        bool Straight(DimensionKind k) => k == DimensionKind.Linear || k == DimensionKind.Horizontal || k == DimensionKind.Vertical;
        return read == asked || (Straight(read) && Straight(asked));
    }
}
