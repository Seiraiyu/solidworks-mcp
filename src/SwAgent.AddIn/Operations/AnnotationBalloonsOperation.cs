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
        var bom = ViewsBill(drawing, facts, view, new HashSet<string>(_items.Values, StringComparer.OrdinalIgnoreCase));
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

    /// <summary>
    /// The bill of materials of <paramref name="view"/>: the one anchored to it, else the one on its sheet that lists its
    /// model in its configuration (SP4 anchors table.bom's bill to the sheet, 5.D live run); null when there is none.
    /// Two that could be the view's are refused: which numbers the balloons is never guessed.
    /// </summary>
    static TableFacts? ViewsBill(ISwDrawing drawing, ViewFacts facts, string view, ICollection<string> items)
    {
        var bills = drawing.Tables(facts.Sheet).Where(t => t.Bom).ToList();
        var own = bills.Where(t => string.Equals(t.View, view, StringComparison.Ordinal)).ToList();
        var candidates = own.Count > 0 ? own : bills.Where(t => t.View == null
            && (t.Model != null ? SameModel(t.Model, facts.Model) : ListsExactly(t, items))
            && (string.IsNullOrEmpty(t.Configuration) || string.IsNullOrEmpty(facts.Configuration) || string.Equals(t.Configuration, facts.Configuration, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count > 1)
            throw ViewRequests.Invalid("/view", $"/view: {string.Join(" and ", candidates.Select(t => t.Name))} on {facts.Sheet} could each be {view}'s bill of materials; delete all but one (table.delete), then balloon it");
        return candidates.SingleOrDefault();
    }

    /// <summary>The same model: the same full path, or (where SOLIDWORKS names it by file) the same file name.</summary>
    static bool SameModel(string bill, string? view) => view != null && (string.Equals(bill, view, StringComparison.OrdinalIgnoreCase)
        || string.Equals(System.IO.Path.GetFileName(bill), System.IO.Path.GetFileName(view), StringComparison.OrdinalIgnoreCase));

    /// <summary>A bill whose rows are exactly the view's items (file and configuration), for one that names no model.</summary>
    static bool ListsExactly(TableFacts bill, ICollection<string> items)
    {
        var rows = new HashSet<string>(bill.Rows.Where(r => r.Path != null).Select(r => BomRows.Key(r.Path!, r.Configuration)), StringComparer.OrdinalIgnoreCase);
        return rows.Count > 0 && rows.SetEquals(items);
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
