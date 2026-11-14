using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// The fake drawing's assembly views, tables and balloons. It behaves as SOLIDWORKS is expected to (and the drawings live
/// suite proves): an assembly view draws each component's box through its transform, shifted by the configuration's
/// explode steps while the view is shown exploded; a bill of materials lists one row per file and configuration of the
/// assembly's components at its level, numbered in order, and none for an empty configuration name (CADapter); Auto
/// Balloon puts one balloon on one instance of each item the view shows, numbered as the view's bill of materials
/// numbers it. Each way SOLIDWORKS can fail or answer wrongly is a flag the tests set.
/// </summary>
sealed partial class FakeDrawing
{
    int _tables;

    /// <summary>Every table on the drawing.</summary>
    public List<TableFacts> Tabled { get; } = new List<TableFacts>();

    public IReadOnlyList<string> VisibleComponents(string view)
    {
        var v = View(view);
        if (v.ShownComponents.Count > 0) return v.ShownComponents.ToList();
        return v.Visible.Select(e => (e.Entity as FakeEntity)?.Component?.Info.Name).Where(n => n != null).Select(n => n!).Distinct().ToList();
    }

    /// <summary>When true, ShowExploded answers true and changes nothing.</summary>
    public bool ShowExplodedIgnored { get; set; }

    /// <summary>When true, a view shown exploded still draws its components collapsed.</summary>
    public bool ExplodedDrawsCollapsed { get; set; }

    /// <summary>When set, the view draws every component this far (sheet mm, x) from where it should.</summary>
    public double ImagesOffByMm { get; set; }

    /// <summary>When true, making a view exploded marks its assembly as changed.</summary>
    public bool ExplodingDirtiesModel { get; set; }

    public bool ShowExploded(string view, bool exploded)
    {
        Calls.Add($"{(exploded ? "explode" : "collapse")} {view}");
        var v = View(view);
        if (!ShowExplodedIgnored) v.Facts.Exploded = exploded;
        if (ExplodingDirtiesModel && v.Model != null) v.Model.Document.Dirty = true;
        return true;
    }

    /// <summary>Each component's box (collapsed, or moved by the configuration's explode steps while exploded) drawn through the view's transform.</summary>
    public IReadOnlyDictionary<string, SheetBox> ComponentImages(string view)
    {
        var v = View(view);
        var images = new Dictionary<string, SheetBox>(StringComparer.OrdinalIgnoreCase);
        var asm = v.Model?.Asm;
        if (asm == null || v.Transform == null) return images;
        foreach (var c in asm.Items.Where(i => i.Info.State == ComponentState.Resolved && i.Info.Parent == null))
        {
            if (v.HiddenComponents.Contains(c.Info.Name) || asm.CollapsedBox(c) is not Box3 box) continue;
            var move = v.Facts.Exploded && !ExplodedDrawsCollapsed ? asm.ExplodedMove(v.Facts.Configuration ?? "Default", c) : new Vector3(0, 0, 0);
            var drawn = ViewGeometry.Project(v.Transform, new Box3
            {
                Min = new Point3(box.Min.X + move.X, box.Min.Y + move.Y, box.Min.Z + move.Z),
                Max = new Point3(box.Max.X + move.X, box.Max.Y + move.Y, box.Max.Z + move.Z),
            });
            images[c.Info.Name] = new SheetBox { Min = new SheetPoint(drawn.Min.X + ImagesOffByMm, drawn.Min.Y), Max = new SheetPoint(drawn.Max.X + ImagesOffByMm, drawn.Max.Y) };
        }
        return images;
    }

    public IReadOnlyList<TableFacts> Tables(string sheet) => Tabled.Where(t => t.Sheet == sheet).Select(t => t.Copy()).ToList();

    /// <summary>When true, InsertBom makes nothing.</summary>
    public bool BomFails { get; set; }

    /// <summary>When true, a bill of materials comes with its header and no rows (what CADapter saw for an empty configuration name).</summary>
    public bool BomHeaderOnly { get; set; }

    /// <summary>When set, each row's quantity cell reads this much more than SOLIDWORKS counted.</summary>
    public int BomQuantityOffBy { get; set; }

    /// <summary>When true, making a bill of materials marks its assembly as changed.</summary>
    public bool BomDirtiesModel { get; set; }

    public string? InsertBom(string view, BomKind kind, string configuration, SheetPoint? at, TableAnchor anchor)
    {
        Calls.Add($"bom {view} {WireNames.ForEnum(kind)} {configuration}");
        var v = View(view);
        if (BomFails || v.Model?.Asm == null) return null;
        var asm = v.Model.Asm;
        var table = new TableFacts
        {
            Name = "Bill of Materials" + ++_tables, Sheet = v.Facts.Sheet, View = view, Bom = true, BomKind = kind, Configuration = configuration, Feature = "Bill of Materials" + _tables,
            Position = at == null ? new SheetPoint(400, 280) : new SheetPoint(at.X, at.Y), Columns = { "ITEM NO.", "PART NUMBER", "DESCRIPTION", "QTY." },
        };
        if (!BomHeaderOnly && configuration.Length > 0)
        {
            var counted = asm.Items.Where(c => c.Info.State != ComponentState.Suppressed && (kind == BomKind.PartsOnly ? !c.Info.Subassembly : c.Info.Parent == null));
            int n = 0;
            foreach (var g in counted.GroupBy(c => BomRows.Key(c.Info.Path!, c.Info.Configuration)))
            {
                string number = (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
                int quantity = g.Count() + BomQuantityOffBy;
                table.Rows.Add(new TableRow
                {
                    Cells = { number, System.IO.Path.GetFileNameWithoutExtension(g.First().Info.Path!), "", quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ItemNumber = number, Quantity = quantity, Path = g.First().Info.Path, Configuration = g.First().Info.Configuration, Components = g.Select(c => c.Info.Name).ToList(),
                });
            }
        }
        Tabled.Add(table);
        if (BomDirtiesModel) v.Model.Document.Dirty = true;
        return table.Name;
    }

    /// <summary>When true, DeleteTables deletes nothing and says so.</summary>
    public bool TableDeleteFails { get; set; }

    /// <summary>When true, DeleteTables says it deleted but leaves them.</summary>
    public bool TableDeleteLeaves { get; set; }

    /// <summary>When set, DeleteTables deletes nothing on this sheet and says so; on other sheets it deletes.</summary>
    public string? TableDeleteFailsOn { get; set; }

    public bool DeleteTables(string sheet, IReadOnlyList<string> names)
    {
        Calls.Add($"delete tables {string.Join(", ", names)}");
        if (TableDeleteFails || sheet == TableDeleteFailsOn || names.Any(n => !Tabled.Any(t => t.Sheet == sheet && t.Name == n))) return false;
        if (!TableDeleteLeaves) Tabled.RemoveAll(t => t.Sheet == sheet && names.Contains(t.Name));
        return true;
    }

    /// <summary>When true, AutoBalloon makes nothing.</summary>
    public bool BalloonsFail { get; set; }

    /// <summary>When true, balloons are numbered from 7 whatever the bill of materials says (what CADapter saw with a bill already on the sheet).</summary>
    public bool BalloonsMisnumbered { get; set; }

    /// <summary>When true, Auto Balloon puts a balloon on every instance rather than one per item.</summary>
    public bool BalloonsEveryInstance { get; set; }

    /// <summary>When true, balloons come attached to nothing.</summary>
    public bool BalloonsUnattached { get; set; }

    public void AutoBalloon(string view, BalloonLayout layout)
    {
        Calls.Add($"balloons {view} {WireNames.ForEnum(layout)}");
        var v = View(view);
        if (BalloonsFail || v.Model?.Asm == null) return;
        var asm = v.Model.Asm;
        var bom = Tabled.FirstOrDefault(t => t.Bom && t.View == view);
        var shown = VisibleComponents(view).Select(asm.Named).ToList();
        var numbers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var already = Annotated.Where(a => a.View == view && a.Kind == AnnotationKind.Balloon).SelectMany(a => a.Components).Select(n => Item(asm.Named(n))).ToList();
        foreach (var c in shown)
        {
            string item = Item(c);
            if (already.Contains(item, StringComparer.OrdinalIgnoreCase) || (!BalloonsEveryInstance && numbers.ContainsKey(item))) continue;
            string number = BalloonsMisnumbered ? (7 + numbers.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : bom?.Rows.FirstOrDefault(r => r.Components.Contains(c.Info.Name))?.ItemNumber ?? (numbers.TryGetValue(item, out var had) ? had : (numbers.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            numbers[item] = number;
            var edge = v.Visible.Select(e => e.Entity).OfType<FakeEntity>().FirstOrDefault(e => e.Component == c);
            Annotated.Add(new AnnotationFacts
            {
                Name = "DetailItem" + ++_annotations, Sheet = v.Facts.Sheet, View = view, Kind = AnnotationKind.Balloon, Text = number, ItemNumber = number,
                Position = new SheetPoint(v.Facts.Position.X + 30, v.Facts.Position.Y + 10 * numbers.Count),
                Attached = edge == null || BalloonsUnattached ? new List<object>() : new List<object> { edge }, Components = BalloonsUnattached ? new List<string>() : new List<string> { c.Info.Name },
            });
        }
    }

    static string Item(FakeComponent c) => BomRows.Key(c.Info.Path!, c.Info.Configuration);
}

sealed partial class FakeAssembly
{
    /// <summary>A component's box collapsed: as it was before the assembly was shown exploded, or as it is.</summary>
    public Box3? CollapsedBox(FakeComponent c) => _collapsedBoxes != null && _collapsedBoxes.TryGetValue(c, out var box) ? box : c.Info.Box;

    /// <summary>How far a configuration's first exploded view with steps moves a component, mm.</summary>
    public Vector3 ExplodedMove(string configuration, FakeComponent c)
    {
        var view = Views.FirstOrDefault(v => v.Configuration == configuration && v.Steps.Count > 0);
        double x = 0, y = 0, z = 0;
        foreach (var s in view?.Steps.Where(s => s.Components.Contains(c)) ?? Enumerable.Empty<FakeExplodeStep>())
        {
            x += s.Move.X;
            y += s.Move.Y;
            z += s.Move.Z;
        }
        return new Vector3(x, y, z);
    }
}
