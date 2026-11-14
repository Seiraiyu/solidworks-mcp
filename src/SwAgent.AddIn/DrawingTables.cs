using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Reads a drawing's tables as results report them (design 611, Assembly drawings), through the seam: every sheet's,
/// each with its reference, its columns and rows as shown, and for a bill of materials each row's file and configuration.
/// </summary>
public static class DrawingTables
{
    /// <summary>Every table of the drawing, sheet by sheet.</summary>
    public static List<TableFacts> All(ISwDrawing drawing) => drawing.SheetNames().SelectMany(drawing.Tables).ToList();

    /// <summary>How many tables the drawing has, on every sheet.</summary>
    public static int Count(ISwDrawing drawing) => All(drawing).Count;

    /// <summary>The table of that sheet and name, read now; null when it is gone.</summary>
    public static TableFacts? Read(ISwDrawing drawing, string sheet, string name) =>
        drawing.Tables(sheet).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>What names a table in its drawing: its sheet and name.</summary>
    public static string Key(TableFacts t) => t.Sheet + "\n" + t.Name;

    /// <summary>The tables in <paramref name="after"/> that <paramref name="before"/> did not have, by sheet and name.</summary>
    public static List<TableFacts> New(IEnumerable<TableFacts> before, IEnumerable<TableFacts> after)
    {
        var had = new HashSet<string>(before.Select(Key), StringComparer.Ordinal);
        return after.Where(t => !had.Contains(Key(t))).ToList();
    }

    /// <summary>A table as results report it.</summary>
    public static TableInfo Info(ReferenceService references, ISwDocument doc, TableFacts t) => new TableInfo
    {
        Name = t.Name,
        Reference = references.IssueTable(doc, t),
        Sheet = t.Sheet,
        View = t.View,
        Kind = t.Bom ? TableKind.Bom : TableKind.Other,
        BomKind = t.BomKind,
        Configuration = t.Configuration,
        Feature = t.Feature,
        Position = t.Position == null ? null : new SheetPoint(t.Position.X, t.Position.Y),
        Columns = new List<string>(t.Columns),
        Rows = t.Copy().Rows,
    };
}
