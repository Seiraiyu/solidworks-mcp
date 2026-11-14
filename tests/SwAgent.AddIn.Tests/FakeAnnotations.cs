using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// The fake drawing's annotations and custom properties. It behaves as SOLIDWORKS is expected to (and the drawings live
/// suite proves): an annotation goes into the view whose entity it was attached to, or the view or sheet asked; a
/// reference dimension reads what the model measures between its entities as the view draws them; a centre mark sits on
/// its circle's drawn centre; a note shows its $PRP:"…" links resolved from the drawing's properties; and SOLIDWORKS
/// selects only what a view shows. Each way SOLIDWORKS can fail or answer wrongly is a flag the tests set.
/// </summary>
sealed partial class FakeDrawing
{
    int _annotations;

    /// <summary>Every annotation on the drawing, each with its sheet and view (null for the sheet's own).</summary>
    public List<AnnotationFacts> Annotated { get; } = new List<AnnotationFacts>();

    /// <summary>The drawing's custom properties, by name, as written.</summary>
    public Dictionary<string, string> Props { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What ImportAnnotations brings: each model annotation (a dimension by the model's name for it) and the view it lands in.</summary>
    public List<(string View, AnnotationKind Kind, string Name)> ImportPlan { get; } = new List<(string, AnnotationKind, string)>();

    /// <summary>When set, an imported dimension reads this much more than the model's value.</summary>
    public double ImportedOffBy { get; set; }

    /// <summary>When true, every annotation SOLIDWORKS is asked to make is not made.</summary>
    public bool AnnotateFails { get; set; }

    /// <summary>When set, a reference dimension reads this much more than the model measures.</summary>
    public double DimensionOffBy { get; set; }

    /// <summary>When true, a dimension made on the drawing drives (it should be driven, a reference dimension).</summary>
    public bool DimensionsDrive { get; set; }

    /// <summary>When set, an annotation made lands this far (sheet mm, x) from where it was put.</summary>
    public double AnnotationLandsOffByMm { get; set; }

    /// <summary>How many times a drawing operation put the drawing back at rest when it ended (Settle).</summary>
    public int Settled { get; private set; }

    public void Settle() => Settled++;

    /// <summary>When set, what each annotation read back says of how its position was read (AnnotationFacts.PositionNote).</summary>
    public string? PositionNote { get; set; }

    /// <summary>When true, a note asked for in a view lands on the sheet.</summary>
    public bool NotesLandOnSheet { get; set; }

    /// <summary>When true, annotations come attached to nothing.</summary>
    public bool AttachIgnored { get; set; }

    /// <summary>When true, a datum tag keeps SOLIDWORKS' own label (Z) rather than the one set.</summary>
    public bool DatumLabelIgnored { get; set; }

    /// <summary>When true, a tolerance frame comes without its datums.</summary>
    public bool GtolDatumsIgnored { get; set; }

    /// <summary>When true, MoveAnnotation and SetNoteText change nothing (and say nothing).</summary>
    public bool AnnotationSetIgnored { get; set; }

    /// <summary>When true, DeleteAnnotations deletes nothing and says so.</summary>
    public bool AnnotationDeleteFails { get; set; }

    /// <summary>When set, DeleteAnnotations deletes nothing in this view (or on this sheet itself) and says so; elsewhere it deletes.</summary>
    public string? AnnotationDeleteFailsIn { get; set; }

    /// <summary>When true, DeleteAnnotations says it deleted but leaves them.</summary>
    public bool AnnotationDeleteLeaves { get; set; }

    /// <summary>When true, SetProperty and DeleteProperty change nothing (and say they did).</summary>
    public bool PropertiesIgnored { get; set; }

    /// <summary>When true, making any annotation marks the views' models as changed.</summary>
    public bool AnnotatingDirtiesModel { get; set; }

    public IReadOnlyList<AnnotationFacts> Annotations(string sheet, string? view) =>
        Annotated.Where(a => a.Sheet == sheet && a.View == view).Select(a => a.Copy()).ToList();

    public void ImportAnnotations(IReadOnlyList<AnnotationImportType> types)
    {
        Calls.Add($"import {string.Join(", ", types.Select(t => WireNames.ForEnum(t)))}");
        foreach (var (view, kind, name) in ImportPlan)
        {
            var v = AllViews.FirstOrDefault(x => x.Facts.Name == view);
            if (v == null || !types.Contains(TypeOf(kind))) continue;
            string? full = kind == AnnotationKind.Dimension && v.Model != null ? name + "@" + Path.GetFileNameWithoutExtension(v.Model.Document.Title) + ".Part" : null;
            if (Annotated.Any(a => a.View == view && (full != null ? a.DimensionName == full : a.Name == name))) continue;
            var facts = new AnnotationFacts { Name = full == null ? name : "Dim" + ++_annotations, Sheet = v.Facts.Sheet, View = view, Kind = kind, Position = new SheetPoint(v.Facts.Position.X, v.Facts.Position.Y) };
            if (full != null)
            {
                var record = v.Model!.Dimensions(null).FirstOrDefault(d => d.FullName == name);
                facts.Value = (record?.Value ?? 0) + ImportedOffBy;
                facts.DimensionName = full;
                facts.DimensionKind = record?.Angle == true ? DimensionKind.Angle : DimensionKind.Linear;
            }
            if (kind == AnnotationKind.Datum) facts.Label = name;
            Annotated.Add(facts);
        }
    }

    static AnnotationImportType TypeOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Dimension => AnnotationImportType.Dimensions,
        AnnotationKind.Datum => AnnotationImportType.Datums,
        AnnotationKind.Gtol => AnnotationImportType.Gtols,
        AnnotationKind.Note => AnnotationImportType.Notes,
        _ => AnnotationImportType.HoleCallouts,
    };

    bool Visible(FakeView view, object entity) => view.Visible.Any(e => ReferenceEquals(e.Entity, entity)) || view.Faces.Contains(entity);

    /// <summary>Adds an annotation as SOLIDWORKS makes one: nothing when told to fail or when an entity is not shown; its name otherwise.</summary>
    string? Make(string? view, string sheet, IReadOnlyList<object> entities, Func<FakeView?, AnnotationFacts> make)
    {
        var v = view == null ? null : View(view);
        if (AnnotateFails || (v != null && entities.Any(e => !Visible(v, e)))) return null;
        var facts = make(v);
        facts.Sheet = v?.Facts.Sheet ?? sheet;
        facts.View ??= view;
        facts.Attached = AttachIgnored ? new List<object>() : entities.ToList();
        if (facts.Position != null) facts.Position = new SheetPoint(facts.Position.X + AnnotationLandsOffByMm, facts.Position.Y);
        facts.PositionNote ??= PositionNote;
        Annotated.Add(facts);
        if (AnnotatingDirtiesModel && v?.Model != null) v.Model.Document.Dirty = true;
        return facts.Name;
    }

    /// <summary>How the fake's SOLIDWORKS draws an entity of a view's model.</summary>
    static ViewEntityInfo Drawn(FakeView view, object entity) =>
        ViewGeometry.Draw(view.Transform!, view.Model!.Describe(entity), view.Model.KindOf(entity) == ReferenceKind.Vertex);

    public string? AddDimension(string view, DimensionKind kind, IReadOnlyList<object> entities, SheetPoint at)
    {
        Calls.Add($"dimension {WireNames.ForEnum(kind)} {view}");
        return Make(view, "", entities, v =>
        {
            var expected = AnnotationGeometry.Expect(kind, entities.Select(e => Drawn(v!, e)).ToList(), v!.Transform!.Scale, out _);
            string name = $"RD{++_annotations}@{view}";
            return new AnnotationFacts
            {
                Name = name, Kind = AnnotationKind.Dimension, Position = new SheetPoint(at.X, at.Y), DimensionKind = kind, DimensionName = name, Driven = !DimensionsDrive,
                Value = (expected?.Value ?? 0) + DimensionOffBy,
            };
        });
    }

    public string? AddNote(string sheet, string? view, string text, SheetPoint at, object? attachTo)
    {
        Calls.Add($"note {view ?? sheet}");
        string? name = Make(view, sheet, attachTo == null ? new List<object>() : new List<object> { attachTo }, v => new AnnotationFacts
        {
            Name = "DetailItem" + ++_annotations, Kind = AnnotationKind.Note, Position = new SheetPoint(at.X, at.Y), Text = Resolve(text), LinkedText = text,
        });
        if (name != null && NotesLandOnSheet) Annotated.Single(a => a.Name == name).View = null;
        return name;
    }

    public string? AddCentreMark(string view, object edge)
    {
        Calls.Add($"centre mark {view}");
        return Make(view, "", new[] { edge }, v => new AnnotationFacts { Name = "Center Mark" + ++_annotations, Kind = AnnotationKind.CentreMark, Position = Drawn(v!, edge).Centre });
    }

    public string? AddCentreLine(string view, IReadOnlyList<object> entities)
    {
        Calls.Add($"centre line {view}");
        return Make(view, "", entities, v => new AnnotationFacts { Name = "Centerline" + ++_annotations, Kind = AnnotationKind.CentreLine });
    }

    public string? AddDatum(string view, object entity, string label, SheetPoint? at)
    {
        Calls.Add($"datum {label} {view}");
        return Make(view, "", new[] { entity }, v => new AnnotationFacts
        {
            Name = "DatumTag" + ++_annotations, Kind = AnnotationKind.Datum, Label = DatumLabelIgnored ? "Z" : label, Position = at ?? new SheetPoint(v!.Facts.Position.X, v.Facts.Position.Y),
        });
    }

    public string? AddGtol(string view, object entity, GtolFrame frame, SheetPoint? at)
    {
        Calls.Add($"gtol {view}");
        return Make(view, "", new[] { entity }, v => new AnnotationFacts
        {
            Name = "Gtol" + ++_annotations, Kind = AnnotationKind.Gtol, Position = at ?? new SheetPoint(v!.Facts.Position.X, v.Facts.Position.Y),
            Gtol = new GtolFrame
            {
                Characteristic = frame.Characteristic, ToleranceMm = frame.ToleranceMm, Diameter = frame.Diameter, MaterialCondition = frame.MaterialCondition,
                Datums = GtolDatumsIgnored ? new List<string>() : new List<string>(frame.Datums),
            },
        });
    }

    AnnotationFacts Find(string sheet, string? view, string name) => Annotated.Single(a => a.Sheet == sheet && a.View == view && a.Name == name);

    public void MoveAnnotation(string sheet, string? view, string name, SheetPoint at)
    {
        Calls.Add($"move annotation {name}");
        if (!AnnotationSetIgnored) Find(sheet, view, name).Position = new SheetPoint(at.X, at.Y);
    }

    public bool SetNoteText(string sheet, string? view, string name, string text)
    {
        Calls.Add($"note text {name}");
        var note = Find(sheet, view, name);
        if (note.Kind != AnnotationKind.Note) return false;
        if (AnnotationSetIgnored) return true;
        note.LinkedText = text;
        note.Text = Resolve(text);
        return true;
    }

    public bool DeleteAnnotations(string sheet, string? view, IReadOnlyList<string> names)
    {
        Calls.Add($"delete annotations {string.Join(", ", names)}");
        if (AnnotationDeleteFails || AnnotationDeleteFailsIn == (view ?? sheet) || names.Any(n => !Annotated.Any(a => a.Sheet == sheet && a.View == view && a.Name == n))) return false;
        if (!AnnotationDeleteLeaves) Annotated.RemoveAll(a => a.Sheet == sheet && a.View == view && names.Contains(a.Name));
        return true;
    }

    public bool Shows(string view, object entity, object? component) => Visible(View(view), entity);

    public IReadOnlyList<object> VisibleFaces(string view) => View(view).Faces.ToList();

    public bool SameEntity(object a, object b) => ReferenceEquals(a, b);

    /// <summary>Entities a view shows as SOLIDWORKS' proxies (ShowAsProxy), each with the model's own entity, or null for none.</summary>
    public Dictionary<object, FakeEntity?> Proxies { get; } = new Dictionary<object, FakeEntity?>();

    static readonly byte[] TwinId = { 7, 7, 7, 7 };

    /// <summary>
    /// Shows <paramref name="own"/> in <paramref name="view"/> as SP4 does (5.C live run, round 2): through a proxy whose
    /// persistent id it shares with every other proxy and which the model does not find; the proxy corresponds to the
    /// model's own entity unless <paramref name="corresponds"/> is false.
    /// </summary>
    public FakeEntity ShowAsProxy(FakeView view, FakeEntity own, bool corresponds = true)
    {
        var i = own.Info;
        var proxy = new FakeEntity(own.Kind, new EntityInfo { Geometry = i.Geometry, Origin = i.Origin, Axis = i.Axis, RadiusMm = i.RadiusMm, LengthMm = i.LengthMm, Feature = i.Feature }) { Id = TwinId, State = PersistState.Invalid };
        Proxies[proxy] = corresponds ? own : null;
        view.Visible.Add(new VisibleEntity(proxy, own.Kind == ReferenceKind.Vertex, false));
        return proxy;
    }

    public IReadOnlyList<object> ModelEntities(string view, object shown) =>
        Proxies.TryGetValue(shown, out var own) ? (own == null ? new List<object>() : new List<object> { own }) : new List<object> { shown };

    public object? InView(string view, object own)
    {
        var v = View(view);
        var proxy = Proxies.Where(p => ReferenceEquals(p.Value, own) && Visible(v, p.Key)).Select(p => p.Key).FirstOrDefault();
        return proxy ?? (Visible(v, own) ? own : null);
    }

    public IReadOnlyList<PropertyFacts> Properties() => Props.Select(p => new PropertyFacts(p.Key, p.Value, Resolve(p.Value))).ToList();

    /// <summary>When set, SetProperty refuses this property (and writes nothing).</summary>
    public string? PropertyRefused { get; set; }

    public bool SetProperty(string name, string value)
    {
        Calls.Add($"property {name}");
        if (string.Equals(name, PropertyRefused, StringComparison.OrdinalIgnoreCase)) return false;
        if (!PropertiesIgnored) Props[name] = value;
        return true;
    }

    public bool DeleteProperty(string name)
    {
        Calls.Add($"delete property {name}");
        return PropertiesIgnored || Props.Remove(name);
    }

    /// <summary>Text with each $PRP:"name" replaced by the drawing's property of that name (empty when it has none).</summary>
    string Resolve(string text) => Regex.Replace(text, "\\$PRP:\"([^\"]*)\"", m => Props.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    /// <summary>An annotation added as if it were already on the drawing.</summary>
    public AnnotationFacts AddAnnotation(AnnotationFacts facts)
    {
        Annotated.Add(facts);
        return facts;
    }
}
