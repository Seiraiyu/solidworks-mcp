using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>A row a bill of materials should have: one component file in one configuration, how many of it, at what depth.</summary>
public sealed class BomExpectation
{
    public BomExpectation(string path, string configuration, int quantity, int level, IReadOnlyList<string> components)
    {
        Path = path;
        Configuration = configuration;
        Quantity = quantity;
        Level = level;
        Components = components;
    }

    public string Path { get; }

    public string Configuration { get; }

    public int Quantity { get; }

    /// <summary>0 at the top level; a subassembly's rows in an indented bill are one deeper.</summary>
    public int Level { get; }

    /// <summary>The components it counts (for an indented bill's subassembly rows, the first instance's).</summary>
    public IReadOnlyList<string> Components { get; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1}) x{2}", System.IO.Path.GetFileName(Path), Configuration, Quantity);
}

/// <summary>
/// The rows a bill of materials should have, counted from the assembly's components as assembly.components reads them
/// (design 611, Assembly drawings: one row per distinct component file and configuration at the asked level, with the
/// quantity assembly.components counts for it), and whether a table read back has exactly those. Suppressed components
/// are not counted, as SOLIDWORKS leaves them out of a bill; rows are compared as a set, in no order.
/// </summary>
public static class BomRows
{
    public static List<BomExpectation> Expected(IReadOnlyList<ComponentInfo> components, BomKind kind)
    {
        var counted = components.Where(c => c.State != ComponentState.Suppressed && c.Path != null).ToList();
        return kind switch
        {
            BomKind.TopLevel => Group(counted.Where(c => c.Parent == null), 0),
            BomKind.PartsOnly => Group(counted.Where(c => !c.Subassembly), 0),
            _ => Indented(counted, null, 0),
        };
    }

    static List<BomExpectation> Indented(IReadOnlyList<ComponentInfo> all, string? parent, int level)
    {
        var rows = new List<BomExpectation>();
        foreach (var row in Group(all.Where(c => string.Equals(c.Parent, parent, StringComparison.OrdinalIgnoreCase)), level))
        {
            rows.Add(row);
            // A subassembly's own rows follow it, counted in one instance of it, as SOLIDWORKS counts them per subassembly.
            var first = all.FirstOrDefault(c => string.Equals(c.Name, row.Components[0], StringComparison.OrdinalIgnoreCase));
            if (first != null && first.Subassembly) rows.AddRange(Indented(all, first.Name, level + 1));
        }
        return rows;
    }

    static List<BomExpectation> Group(IEnumerable<ComponentInfo> components, int level) =>
        components.GroupBy(c => Key(c.Path!, c.Configuration), StringComparer.OrdinalIgnoreCase)
            .Select(g => new BomExpectation(g.First().Path!, g.First().Configuration ?? "", g.Count(), level, g.Select(c => c.Name).ToList()))
            .ToList();

    /// <summary>What names a row: its file and configuration, without regard to case.</summary>
    public static string Key(string path, string? configuration) => path.ToUpperInvariant() + "|" + (configuration ?? "").ToUpperInvariant();

    /// <summary>
    /// Null when <paramref name="rows"/> are exactly <paramref name="expected"/>: the same files and configurations, each
    /// with its quantity (the row's quantity cell, or how many components SOLIDWORKS lists for it when it has none);
    /// otherwise what differs, as one sentence.
    /// </summary>
    public static string? Mismatch(IReadOnlyList<BomExpectation> expected, IReadOnlyList<TableRow> rows)
    {
        if (rows.Count == 0) return "the table has no rows";
        var unnamed = rows.Select((r, i) => (r, i)).Where(x => x.r.Path == null).Select(x => x.i + 1).ToList();
        if (unnamed.Count > 0) return $"row {string.Join(", ", unnamed)} names no single component file";
        var want = expected.Select(e => (Key: Key(e.Path, e.Configuration) + "|" + e.Quantity.ToString(CultureInfo.InvariantCulture), Text: e.ToString())).ToList();
        var have = rows.Select(r => (Key: Key(r.Path!, r.Configuration) + "|" + Quantity(r).ToString(CultureInfo.InvariantCulture), Text: Describe(r))).ToList();
        var missing = Without(want, have);
        var extra = Without(have, want);
        if (missing.Count == 0 && extra.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add("missing " + string.Join(", ", missing));
        if (extra.Count > 0) parts.Add("not expected " + string.Join(", ", extra));
        return string.Join("; ", parts);
    }

    /// <summary>A row's quantity: its quantity cell, else how many components SOLIDWORKS lists for it.</summary>
    public static int Quantity(TableRow row) => row.Quantity ?? row.Components.Count;

    /// <summary>A row read back as the bill's expectations print: file (configuration) xN.</summary>
    public static string Describe(TableRow row) =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1}) x{2}", row.Path == null ? "no file" : System.IO.Path.GetFileName(row.Path), row.Configuration ?? "", Quantity(row));

    /// <summary>The whole number a quantity cell shows, or null.</summary>
    public static int? ParseQuantity(string? text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : (int?)null;

    /// <summary>The texts of <paramref name="from"/> left when each of <paramref name="take"/> takes one of the same key.</summary>
    static List<string> Without(List<(string Key, string Text)> from, List<(string Key, string Text)> take)
    {
        var left = new List<(string Key, string Text)>(from);
        foreach (var t in take)
        {
            int i = left.FindIndex(x => string.Equals(x.Key, t.Key, StringComparison.Ordinal));
            if (i >= 0) left.RemoveAt(i);
        }
        return left.Select(x => x.Text).ToList();
    }
}
