using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class AnnotationBalloonsOperation : DrawingChangeOperation<AnnotationBalloonsRequest, AnnotationBalloonsResult>
{
    public AnnotationBalloonsOperation(AddInServices services)
        : base(services)
    {
    }

    string _view = "";
    List<string> _visible = new List<string>();
    Dictionary<string, string> _items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string>? _bom;
    List<AnnotationFacts> _made = new List<AnnotationFacts>();
    List<AnnotationFacts> _already = new List<AnnotationFacts>();

    protected override string? DocumentOf(AnnotationBalloonsRequest request) => request.Document;

    /// <summary>A component's item: its file and configuration, as a bill of materials groups them (BomRows.Key).</summary>
    string ItemOf(string component) => _items.TryGetValue(component, out var item) ? item : "?" + component;

    protected override AnnotationBalloonsResult Change(ISwDocument doc, ISwDrawing drawing, AnnotationBalloonsRequest request, ModelTransaction tx, IOperationContext context)
    {
        string view = _view = View(doc, drawing, request.View, "/view");
        var (facts, _, asm, _) = Tabling.Assembly(drawing, view);
        _visible = drawing.VisibleComponents(view).ToList();
        if (_visible.Count == 0) throw ViewRequests.Invalid("/view", $"/view: {view} shows no component to balloon");
        _items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _visible)
        {
            if (asm.ComponentByName(name) is object c && asm.Read(c) is ComponentInfo info && info.Path != null) _items[name] = BomRows.Key(info.Path, info.Configuration);
        }
        // The view's bill of materials, when it has one: each item's number is its row's.
        var bom = drawing.Tables(facts.Sheet).FirstOrDefault(t => t.Bom && string.Equals(t.View, view, StringComparison.Ordinal));
        _bom = bom == null ? null : bom.Rows.Where(r => r.Path != null && r.ItemNumber != null)
            .GroupBy(r => BomRows.Key(r.Path!, r.Configuration), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ItemNumber!, StringComparer.OrdinalIgnoreCase);

        var before = drawing.Annotations(facts.Sheet, view);
        _already = before.Where(a => a.Kind == AnnotationKind.Balloon).ToList();
        drawing.AutoBalloon(view, request.Layout ?? BalloonLayout.Square);
        _made = DrawingAnnotations.New(before, drawing.Annotations(facts.Sheet, view));
        foreach (var made in _made) MadeAnnotation(doc, drawing, tx, made);
        var ballooned = new HashSet<string>(_already.Where(a => a.Components.Count == 1).Select(a => ItemOf(a.Components[0])), StringComparer.OrdinalIgnoreCase);
        int items = _visible.Select(ItemOf).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int already = _visible.Select(ItemOf).Distinct(StringComparer.OrdinalIgnoreCase).Count(ballooned.Contains);
        if (_made.Count == 0 && already < items)
        {
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no balloons in {view}, though {items - already} of its {items} items have none",
                new JsonObject { ["view"] = view, ["items"] = items, ["already"] = already });
        }
        context.Log.Info($"annotation.balloons: {view}: {_made.Count} made ({string.Join(", ", _made.Select(m => $"{m.Name} {m.ItemNumber} on {string.Join("/", m.Components)}"))}), {already} of {items} items already ballooned{(bom == null ? "" : ", numbered to " + bom.Name)}");
        return new AnnotationBalloonsResult { Items = items, Already = already, Bom = bom?.Name };
    }

    /// <summary>Walks the view's annotations: one balloon per item the view shows, each on a component it shows, numbered as the bill numbers its row.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, AnnotationBalloonsRequest request, AnnotationBalloonsResult result)
    {
        var made = _made.Select(m => Annotating.Reread(drawing, m)).ToList();
        var other = made.FirstOrDefault(m => m.Kind != AnnotationKind.Balloon);
        if (other != null) throw Annotating.Failed(other.Name, "kind", "balloon", WireNames.ForEnum(other.Kind));
        static BalloonFacts Facts(AnnotationFacts a) => new BalloonFacts(a.Name, a.ItemNumber, a.Components) { AttachedNote = a.AttachedNote };
        if (BalloonCheck.Mismatch(_visible, ItemOf, made.Select(Facts).ToList(), _already.Select(Facts).ToList(), _bom) is var (check, balloon, expected, measured))
            throw Annotating.Failed(balloon.Length > 0 ? balloon : "in " + _view, check, expected, measured);
        var shown = new ShownEntities(Services.References, drawing);
        var owners = DrawingAnnotations.Owners(drawing);
        result.Made = made.Select(m => DrawingAnnotations.Info(Services.References, doc, drawing, m, owners, shown)).ToList();
    }
}
