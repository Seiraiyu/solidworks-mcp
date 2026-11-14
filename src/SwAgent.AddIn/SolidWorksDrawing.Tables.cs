using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using TableAnchor = SwAgent.Contracts.TableAnchor;

namespace SwAgent.AddIn;

/// <summary>
/// Assembly drawings (design 611, Assembly drawings; plan 5.D): the components an assembly view shows and what it draws
/// of each, exploded views, tables and bills of materials, and balloons. Tables and views are selected by name or through
/// their own annotation, never passed to SOLIDWORKS in an array; the arrays SOLIDWORKS hands back (GetVisibleComponents,
/// GetPolylines7's edges, GetTableAnnotations, IBomTableAnnotation.GetComponents2) are only read. Covered by the drawings
/// live suite.
/// </summary>
internal sealed partial class SolidWorksDrawing
{
    static IEnumerable<IComponent2> Components(IView view) =>
        view.GetVisibleComponents() is object[] all ? all.OfType<IComponent2>() : Enumerable.Empty<IComponent2>();

    /// <summary>
    /// The root segment SP4 puts before the names of a view's components (pinblock-2 in pinblock-2/pin-1), or null:
    /// names are given as the assembly names them, without it (ViewComponentNames).
    /// </summary>
    static string? RootOf(IView view) =>
        ViewComponentNames.Root(Components(view).Select(c => c.Name2).Where(n => !string.IsNullOrEmpty(n)));

    /// <summary>A component's name as the assembly names it, without the view's root.</summary>
    static string? NameIn(IComponent2? component, string? root) =>
        component?.Name2 is string name && name.Length > 0 ? ViewComponentNames.Unrooted(name, root) : null;

    /// <summary>The component a view shows of that name (the assembly's, or SOLIDWORKS' own); null for none, or for no name.</summary>
    static Component2? ComponentIn(IView view, string? name)
    {
        if (name == null) return null;
        string? root = RootOf(view);
        return Components(view).FirstOrDefault(c => string.Equals(NameIn(c, root), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase)) as Component2;
    }

    public IReadOnlyList<string> VisibleComponents(string view)
    {
        var v = View(view);
        string? root = RootOf(v);
        return Components(v).Select(c => NameIn(c, root)).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyDictionary<string, SheetBox> ComponentImages(string view)
    {
        var v = View(view);
        var facts = ReadView(view)!;
        var edges = v.GetPolylines7(0, out object polylines) as object[] ?? new object[0];
        var numbers = polylines as double[] ?? new double[0];
        string? root = RootOf(v);
        var owners = edges.Select(e => NameIn((e as IEntity)?.GetComponent() as IComponent2, root)).ToList();
        try
        {
            return ExplodedImages.PerComponent(owners, numbers, facts.Position, facts.Scale.Numerator / facts.Scale.Denominator);
        }
        catch (FormatException e)
        {
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS' polylines for {view}'s components do not read: {e.Message} (IView.GetPolylines7 gave {edges.Length} edges and {numbers.Length} numbers)",
                new SwAgent.Contracts.Serialization.JsonObject { ["view"] = view });
        }
    }

    public bool ShowExploded(string view, bool exploded)
    {
        bool shown = View(view).ShowExploded(exploded);
        _doc.GraphicsRedraw2();
        return shown;
    }

    /// <summary>The views of a sheet that can hold tables: its own view first, then its views, as IDrawingDoc.GetViews lists them.</summary>
    IEnumerable<(IView View, bool Own)> TableOwners(string sheet)
    {
        if (!(_drawing.GetViews() is object[] sheets)) yield break;
        foreach (var s in sheets)
        {
            if (!(s is object[] views) || views.Length == 0 || !(views[0] is IView own)) continue;
            if (!string.Equals(own.Sheet?.GetName() ?? own.GetName2(), sheet, StringComparison.Ordinal)) continue;
            yield return (own, true);
            foreach (var v in views.Skip(1).OfType<IView>()) yield return (v, false);
        }
    }

    /// <summary>Every table annotation of a sheet, each once by name, with the view it is anchored to (null for the sheet's own).</summary>
    IEnumerable<(ITableAnnotation Table, string Name, string? View)> TablesOf(string sheet)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (owner, own) in TableOwners(sheet))
        {
            if (!(owner.GetTableAnnotations() is object[] tables)) continue;
            foreach (var t in tables.OfType<ITableAnnotation>())
            {
                string name = t.GetAnnotation()?.GetName() ?? "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                yield return (t, name, own ? null : owner.GetName2());
            }
        }
    }

    public IReadOnlyList<TableFacts> Tables(string sheet) => TablesOf(sheet).Select(t => ReadTable(t.Table, t.Name, sheet, t.View)).ToList();

    TableFacts ReadTable(ITableAnnotation table, string name, string sheet, string? view)
    {
        var facts = new TableFacts
        {
            Name = name, Sheet = sheet, View = view,
            Position = table.GetAnnotation()?.GetPosition() is double[] p && p.Length >= 2 ? new SheetPoint(p[0] * Mm, p[1] * Mm) : null,
        };
        string? root = view != null && Find(view) is IView anchored ? RootOf(anchored) : null;
        int rows = table.RowCount, columns = table.ColumnCount, header = table.GetHeaderCount();
        bool headerBelow = table.GetHeaderStyle() == (int)swTableHeaderPosition_e.swTableHeader_Bottom;
        for (int c = 0; c < columns; c++) facts.Columns.Add(table.GetColumnTitle2(c, false) ?? "");
        var bom = table.Type == (int)swTableAnnotationType_e.swTableAnnotation_BillOfMaterials ? table as IBomTableAnnotation : null;
        int quantity = -1, item = -1;
        if (bom != null)
        {
            var feature = bom.BomFeature;
            facts.Bom = true;
            facts.Configuration = feature?.Configuration;
            facts.Feature = feature?.Name;
            facts.BomKind = feature == null ? (BomKind?)null : BomKindOf(feature.TableType);
            for (int c = 0; c < columns; c++)
            {
                int type = table.GetColumnType2(c, false);
                if (type == (int)swTableColumnTypes_e.swBomTableColumnType_Quantity && quantity < 0) quantity = c;
                if (type == (int)swTableColumnTypes_e.swBomTableColumnType_ItemNumber && item < 0) item = c;
            }
        }
        int first = headerBelow ? 0 : header, last = headerBelow ? rows - header : rows;
        for (int r = first; r < last; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < columns; c++) row.Cells.Add(table.get_DisplayedText(r, c) ?? "");
            if (bom != null)
            {
                string configuration = facts.Configuration ?? "";
                bom.GetComponentsCount2(r, configuration, out string itemNumber, out string _);
                row.ItemNumber = !string.IsNullOrEmpty(itemNumber) ? itemNumber : item >= 0 ? row.Cells[item] : null;
                row.Quantity = quantity >= 0 ? BomRows.ParseQuantity(row.Cells[quantity]) : null;
                var parts = bom.GetComponents2(r, configuration) is object[] listed ? listed.OfType<IComponent2>().ToList() : new List<IComponent2>();
                row.Components.AddRange(parts.Select(x => NameIn(x, root) ?? ""));
                var files = parts.Select(x => x.GetPathName() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var configurations = parts.Select(x => x.ReferencedConfiguration ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                row.Path = files.Count == 1 && files[0].Length > 0 ? files[0] : null;
                row.Configuration = configurations.Count == 1 ? configurations[0] : null;
            }
            facts.Rows.Add(row);
        }
        return facts;
    }

    /// <summary>swBomType_e as design 611 names it; null for a flattened bill, which the add-in does not make.</summary>
    internal static BomKind? BomKindOf(int swType) => swType switch
    {
        (int)swBomType_e.swBomType_TopLevelOnly => BomKind.TopLevel,
        (int)swBomType_e.swBomType_PartsOnly => BomKind.PartsOnly,
        (int)swBomType_e.swBomType_Indented => BomKind.Indented,
        _ => null,
    };

    /// <summary>swBomType_e for a kind.</summary>
    internal static int BomType(BomKind kind) => kind switch
    {
        BomKind.PartsOnly => (int)swBomType_e.swBomType_PartsOnly,
        BomKind.Indented => (int)swBomType_e.swBomType_Indented,
        _ => (int)swBomType_e.swBomType_TopLevelOnly,
    };

    /// <summary>swBOMConfigurationAnchorType_e for a corner.</summary>
    internal static int AnchorType(TableAnchor anchor) => anchor switch
    {
        TableAnchor.TopRight => (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
        TableAnchor.BottomLeft => (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomLeft,
        TableAnchor.BottomRight => (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomRight,
        _ => (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft,
    };

    /// <summary>
    /// SOLIDWORKS' standard bill of materials template (bom-standard.sldbomtbt) in the first of the user's BOM template
    /// folders (Tools > Options > File Locations) that has it; empty, for SOLIDWORKS' own default, when none does.
    /// </summary>
    string BomTemplate()
    {
        string folders = _sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsBOMTemplates) ?? "";
        foreach (var folder in folders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(folder.Trim(), "bom-standard.sldbomtbt");
            if (File.Exists(path)) return path;
        }
        return "";
    }

    public string? InsertBom(string view, BomKind kind, string configuration, SheetPoint? at, TableAnchor anchor)
    {
        var v = View(view);
        if (v.Sheet?.GetName() is string sheet) ShowSheet(sheet);
        _doc.ClearSelection2(true);
        try
        {
            int numbering = kind == BomKind.Indented ? (int)swNumberingType_e.swNumberingType_Detailed : (int)swNumberingType_e.swNumberingType_None;
            var made = v.InsertBomTable4(at == null, (at?.X ?? 0) / Mm, (at?.Y ?? 0) / Mm, AnchorType(anchor), BomType(kind), configuration, BomTemplate(), false, numbering, false);
            return (made as ITableAnnotation)?.GetAnnotation()?.GetName();
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public bool DeleteTables(string sheet, IReadOnlyList<string> names)
    {
        ShowSheet(sheet);
        try
        {
            foreach (string name in names)
            {
                var found = TablesOf(sheet).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
                if (found.Table == null) return false;
                _doc.ClearSelection2(true);
                if (found.Table.GetAnnotation() is IAnnotation a && a.Select3(false, null)) _doc.Extension.DeleteSelection2(0);
                if (!TablesOf(sheet).Any(t => string.Equals(t.Name, name, StringComparison.Ordinal))) continue;
                // A bill of materials is a feature of the drawing: deleted by selecting it when its annotation's selection leaves it.
                _doc.ClearSelection2(true);
                if ((found.Table as IBomTableAnnotation)?.BomFeature?.GetFeature() is IFeature feature && feature.Select2(false, 0)) _doc.Extension.DeleteSelection2(0);
                if (TablesOf(sheet).Any(t => string.Equals(t.Name, name, StringComparison.Ordinal))) return false;
            }
            return true;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    /// <summary>swBalloonLayoutType_e for a layout.</summary>
    internal static int LayoutType(BalloonLayout layout) => layout switch
    {
        BalloonLayout.Circular => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Circle,
        BalloonLayout.Top => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Top,
        BalloonLayout.Bottom => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Bottom,
        BalloonLayout.Left => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Left,
        BalloonLayout.Right => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Right,
        _ => (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square,
    };

    public void AutoBalloon(string view, BalloonLayout layout)
    {
        var v = View(view);
        if (v.Sheet?.GetName() is string sheet) ShowSheet(sheet);
        _doc.ClearSelection2(true);
        try
        {
            if (!_doc.Extension.SelectByID2(view, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0)) return;
            var options = _drawing.CreateAutoBalloonOptions();
            options.Layout = LayoutType(layout);
            options.IgnoreMultiple = true;
            options.Style = (int)swBalloonStyle_e.swBS_Circular;
            options.Size = (int)swBalloonFit_e.swBF_2Chars;
            options.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
            options.ItemOrder = (int)swBalloonItemNumbersOrder_e.swBalloonItemNumbers_DoNotChangeItemNumbers;
            _drawing.AutoBalloon5(options);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }
}
