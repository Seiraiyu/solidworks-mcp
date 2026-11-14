using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationNoteOperation : DrawingChangeOperation<AnnotationNoteRequest, AnnotationNoteResult>
{
    public AnnotationNoteOperation(AddInServices services)
        : base(services)
    {
    }

    AnnotationFacts _made = new AnnotationFacts();
    string? _view;
    ResolvedViewEntity? _attach;

    protected override string? DocumentOf(AnnotationNoteRequest request) => request.Document;

    protected override AnnotationNoteResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationNoteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw ViewRequests.Invalid("/text", "/text: a note needs text");
        _attach = request.AttachTo == null ? null : ViewEntities.ResolveShown(Services.References, doc, drawing, request.AttachTo, "/attach_to", ReferenceKind.Edge, ReferenceKind.Vertex, ReferenceKind.Face);
        string? asked = request.View == null ? null : View(doc, drawing, request.View, "/view");
        if (_attach != null && asked != null && asked != _attach.View)
            throw ViewRequests.Invalid("/view", $"/view: the note's leader points into {_attach.View}, so the note goes there, not into {asked}; leave view out");
        _view = _attach?.View ?? asked;
        string sheet = _view != null ? drawing.ReadView(_view)!.Sheet : request.Sheet != null ? Sheet(doc, drawing, request.Sheet, "/sheet") : drawing.ActiveSheet() ?? drawing.SheetNames().First();
        if (_view != null && request.Sheet != null && Sheet(doc, drawing, request.Sheet, "/sheet") != sheet)
            throw ViewRequests.Invalid("/sheet", $"/sheet: {_view} is on {sheet}; leave sheet out for a note in a view");
        ViewRequests.OnSheet(request.At, drawing.ReadSheet(sheet)!, "/at");

        var before = DrawingAnnotations.All(drawing);
        string? name = drawing.AddNote(sheet, _view, request.Text, request.At, _attach?.Entity);
        var made = DrawingAnnotations.New(before, DrawingAnnotations.All(drawing));
        foreach (var a in made) MadeAnnotation(doc, drawing, tx, a);
        _made = Annotating.Mine(made, name, $"note {(_view == null ? "on " + sheet : "in " + _view)}");
        context.Log.Info($"annotation.note: {_made.Name} in {DrawingAnnotations.Where(_made)} at {request.At}{(_attach == null ? "" : ", its leader to an entity of " + _attach.View)}");
        return new AnnotationNoteResult();
    }

    /// <summary>Design 611: the note's text and position read back, it is where it was asked, and its leader is attached to the entity asked.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationNoteRequest request, AnnotationNoteResult result)
    {
        var a = Annotating.Reread(drawing, _made);
        if (a.Kind != AnnotationKind.Note) throw Annotating.Failed(a.Name, "kind", "a note", WireNames.ForEnum(a.Kind));
        if (a.View != _view) throw Annotating.Failed(a.Name, "owner", _view ?? $"the sheet {a.Sheet} itself", a.View ?? $"the sheet {a.Sheet} itself");
        string written = string.IsNullOrEmpty(a.LinkedText) ? a.Text ?? "" : a.LinkedText!;
        if (!string.Equals(written, request.Text, StringComparison.Ordinal)) throw Annotating.Failed(a.Name, "text", request.Text, written);
        if (a.Position == null || !ViewGeometry.Near(a.Position, request.At)) throw Annotating.Failed(a.Name, "position", request.At.ToString(), a.Position?.ToString() ?? "none");
        if (_attach != null && !a.Attached.Any(o => drawing.SameEntity(o, _attach.Entity)))
            throw Annotating.Failed(a.Name, "attached", $"a leader to the entity of {_attach.View} asked", a.Attached.Count == 0 ? "no leader attached" : $"attached to {a.Attached.Count} other entities");
        result.Annotation = DescribeAnnotation(doc, drawing, a);
        result.Text = written;
        result.Shown = a.Text ?? written;
    }
}
