using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What datum tags and tolerance frames share: the drawing's datum labels, and the checks of an attached symbol.</summary>
static class Symbols
{
    /// <summary>Every datum tag's label on the drawing, upper case, with where it is.</summary>
    public static List<(string Label, string Where)> DatumLabels(ISwDrawing drawing) =>
        DrawingAnnotations.All(drawing).Where(a => a.Kind == AnnotationKind.Datum && !string.IsNullOrEmpty(a.Label))
            .Select(a => (a.Label!.Trim().ToUpperInvariant(), DrawingAnnotations.Where(a))).ToList();

    /// <summary>The symbol is attached to the entity asked, and at at within 0.01 mm when at was given.</summary>
    public static void Placed(ISwDrawing drawing, AnnotationFacts a, ResolvedViewEntity attach, SheetPoint? at)
    {
        if (!a.Attached.Any(o => drawing.SameEntity(o, attach.Entity)))
            throw Annotating.Failed(a.Name, "attached", $"attached to the entity of {attach.View} asked", a.Attached.Count == 0 ? "attached to nothing" : $"attached to {a.Attached.Count} other entities");
        if (at != null && (a.Position == null || !ViewGeometry.Near(a.Position, at)))
            throw Annotating.Failed(a.Name, "position", at.ToString(), a.Position?.ToString() ?? "none");
    }
}

public sealed class AnnotationDatumOperation : DrawingChangeOperation<AnnotationDatumRequest, AnnotationResult>
{
    public AnnotationDatumOperation(AddInServices services)
        : base(services)
    {
    }

    AnnotationFacts _made = new AnnotationFacts();
    ResolvedViewEntity? _attach;
    string _label = "";

    protected override string? DocumentOf(AnnotationDatumRequest request) => request.Document;

    protected override AnnotationResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationDatumRequest request, ModelTransaction tx, IOperationContext context)
    {
        _label = AnnotationGeometry.DatumLabel(request.Label)
            ?? throw ViewRequests.Invalid("/label", $"/label: a datum's label is 1 to 3 letters A to Z without I, O and Q (ASME Y14.5), not '{request.Label}'");
        var taken = Symbols.DatumLabels(drawing).FirstOrDefault(l => l.Label == _label);
        if (taken.Label != null) throw ViewRequests.Invalid("/label", $"/label: {_label} already labels the datum tag in {taken.Where}; give another");
        _attach = ViewEntities.ResolveShown(Services.References, doc, drawing, request.AttachTo, "/attach_to", ReferenceKind.Edge, ReferenceKind.Face);
        if (request.At != null) ViewRequests.OnSheet(request.At, drawing.ReadSheet(drawing.ReadView(_attach.View)!.Sheet)!, "/at");

        var before = DrawingAnnotations.All(drawing);
        string? name = drawing.AddDatum(_attach.View, _attach.Entity, _label, request.At);
        var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
        foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
        _made = Annotating.Mine(made, name, $"datum tag {_label} in {_attach.View}");
        context.Log.Info($"annotation.datum: {_made.Name} ({_label}) in {_attach.View}{(request.At == null ? "" : " at " + request.At)}");
        return new AnnotationResult();
    }

    /// <summary>Design 611: the tag reads back its label and is attached to the entity asked.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationDatumRequest request, AnnotationResult result)
    {
        var a = Annotating.Reread(drawing, _made);
        if (a.Kind != AnnotationKind.Datum) throw Annotating.Failed(a.Name, "kind", "a datum tag", WireNames.ForEnum(a.Kind));
        if (!string.Equals(a.Label?.Trim(), _label, StringComparison.OrdinalIgnoreCase)) throw Annotating.Failed(a.Name, "label", _label, a.Label ?? "none");
        Symbols.Placed(drawing, a, _attach!, request.At);
        result.Annotation = DescribeAnnotation(doc, drawing, a);
    }
}

public sealed class AnnotationGtolOperation : DrawingChangeOperation<AnnotationGtolRequest, AnnotationResult>
{
    public AnnotationGtolOperation(AddInServices services)
        : base(services)
    {
    }

    AnnotationFacts _made = new AnnotationFacts();
    ResolvedViewEntity? _attach;
    GtolFrame _frame = new GtolFrame();

    protected override string? DocumentOf(AnnotationGtolRequest request) => request.Document;

    protected override AnnotationResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationGtolRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (!(request.ToleranceMm > 0) || double.IsInfinity(request.ToleranceMm))
            throw ViewRequests.Invalid("/tolerance_mm", FormattableString.Invariant($"/tolerance_mm: a tolerance is more than 0 mm, not {request.ToleranceMm}"));
        var given = request.Datums ?? new List<string>();
        if (given.Count > 3) throw ViewRequests.Invalid("/datums", $"/datums: a frame takes up to three datums (primary, secondary, tertiary), not {given.Count}");
        if (given.Count > 0 && AnnotationGeometry.IsForm(request.Characteristic))
            throw ViewRequests.Invalid("/datums", $"/datums: {WireNames.ForEnum(request.Characteristic)} is a form tolerance, which takes no datum");
        var labels = Symbols.DatumLabels(drawing);
        var datums = new List<string>();
        for (int i = 0; i < given.Count; i++)
        {
            string label = AnnotationGeometry.DatumLabel(given[i]) ?? throw ViewRequests.Invalid($"/datums/{i}", $"/datums/{i}: '{given[i]}' is not a datum label (1 to 3 letters A to Z without I, O and Q)");
            if (datums.Contains(label)) throw ViewRequests.Invalid($"/datums/{i}", $"/datums/{i}: {label} is named twice");
            if (!labels.Any(l => l.Label == label))
            {
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"/datums/{i}: no datum tag on {doc.Info().Title} is labelled {label} ({(labels.Count == 0 ? "it has none" : "it has " + string.Join(", ", labels.Select(l => l.Label)))}); make it with annotation.datum first",
                    new JsonObject { ["field"] = $"/datums/{i}", ["datums"] = labels.Select(l => (object?)l.Label).ToList() });
            }
            datums.Add(label);
        }
        _attach = ViewEntities.ResolveShown(Services.References, doc, drawing, request.AttachTo, "/attach_to", ReferenceKind.Edge, ReferenceKind.Face);
        if (request.At != null) ViewRequests.OnSheet(request.At, drawing.ReadSheet(drawing.ReadView(_attach.View)!.Sheet)!, "/at");
        _frame = new GtolFrame
        {
            Characteristic = request.Characteristic, ToleranceMm = request.ToleranceMm, Diameter = request.Diameter == true,
            MaterialCondition = request.MaterialCondition ?? MaterialCondition.None, Datums = datums,
        };

        var before = DrawingAnnotations.All(drawing);
        string? name = drawing.AddGtol(_attach.View, _attach.Entity, _frame, request.At);
        var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
        foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
        _made = Annotating.Mine(made, name, $"{WireNames.ForEnum(request.Characteristic)} tolerance frame in {_attach.View}");
        context.Log.Info($"annotation.gtol: {_made.Name} in {_attach.View}: {Describe(_frame)}");
        return new AnnotationResult();
    }

    /// <summary>Design 611: the frame reads back its symbol, tolerance, datums and condition, and is attached to the entity asked.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationGtolRequest request, AnnotationResult result)
    {
        var a = Annotating.Reread(drawing, _made);
        if (a.Kind != AnnotationKind.Gtol || a.Gtol == null) throw Annotating.Failed(a.Name, "kind", "a tolerance frame", WireNames.ForEnum(a.Kind));
        var f = a.Gtol;
        if (f.Characteristic != _frame.Characteristic) throw Annotating.Failed(a.Name, "characteristic", Describe(_frame), Describe(f));
        if (f.ToleranceMm == null || Math.Abs(f.ToleranceMm.Value - _frame.ToleranceMm!.Value) > 1e-9) throw Annotating.Failed(a.Name, "tolerance", Describe(_frame), Describe(f));
        if (f.Diameter != _frame.Diameter) throw Annotating.Failed(a.Name, "diameter", Describe(_frame), Describe(f));
        if (f.MaterialCondition != _frame.MaterialCondition) throw Annotating.Failed(a.Name, "material_condition", Describe(_frame), Describe(f));
        if (!f.Datums.Select(d => d.ToUpperInvariant()).SequenceEqual(_frame.Datums)) throw Annotating.Failed(a.Name, "datums", Describe(_frame), Describe(f));
        Symbols.Placed(drawing, a, _attach!, request.At);
        result.Annotation = DescribeAnnotation(doc, drawing, a);
    }

    static string Describe(GtolFrame f) =>
        FormattableString.Invariant($"{(f.Characteristic == null ? "an unnamed symbol" : WireNames.ForEnum(f.Characteristic.Value))} {(f.Diameter ? "Ø" : "")}{(f.ToleranceMm == null ? "?" : AnnotationGeometry.ToleranceText(f.ToleranceMm.Value))}{(f.MaterialCondition == null ? " (an unnamed condition)" : f.MaterialCondition == MaterialCondition.None ? "" : " " + WireNames.ForEnum(f.MaterialCondition.Value))}{(f.Datums.Count == 0 ? "" : " | " + string.Join(" | ", f.Datums))}");
}
