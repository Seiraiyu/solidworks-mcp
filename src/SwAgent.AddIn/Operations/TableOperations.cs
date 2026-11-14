using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the table operations share: the assembly a view shows, and how a check that fails is answered.</summary>
static class Tabling
{
    /// <summary>verification_failed for a table that failed a check, with what was expected and what was measured.</summary>
    public static SwAgentException Failed(string table, string check, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"the table {table} failed its {check} check: expected {expected}, measured {measured}",
            new JsonObject { ["check"] = check, ["table"] = table, ["expected"] = expected, ["measured"] = measured });

    /// <summary>
    /// The assembly a view shows, in the configuration it shows, which must be the assembly's active one (what
    /// assembly.components reads). invalid_argument naming /view otherwise.
    /// </summary>
    public static (ViewFacts Facts, ISwDocument Model, ISwAssembly Assembly, string Configuration) Assembly(ISwDrawing drawing, string view)
    {
        var facts = drawing.ReadView(view)!;
        var model = drawing.ModelOf(view) ?? throw ViewRequests.Invalid("/view", $"/view: {view} shows no model that is loaded");
        var asm = model.Assembly() ?? throw ViewRequests.Invalid("/view", $"/view: {view} shows {model.Info().Title}, a part; a bill of materials and balloons are for a view of an assembly");
        string active = model.ActiveConfiguration();
        string configuration = string.IsNullOrWhiteSpace(facts.Configuration) ? active : facts.Configuration!;
        if (!string.Equals(configuration, active, StringComparison.OrdinalIgnoreCase))
        {
            throw ViewRequests.Invalid("/view",
                $"/view: {view} shows {configuration} of {model.Info().Title}, but its active configuration is {active}, and its components are read (assembly.components) in the active configuration; activate {configuration} with sw_configuration_activate on {model.Info().Title} first");
        }
        return (facts, model, asm, configuration);
    }
}

public sealed class TableReadOperation : Operation<TableReadRequest, TableReadResult>
{
    readonly AddInServices _services;

    public TableReadOperation(AddInServices services)
    {
        _services = services;
    }

    protected override TableReadResult Run(TableReadRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var result = new TableReadResult { Tables = Listed(_services.References, doc, drawing, request).Select(t => DrawingTables.Info(_services.References, doc, t)).ToList() };
        context.Log.Info($"table.read: {result.Tables.Count} tables, {result.Tables.Sum(t => t.Rows.Count)} rows");
        return result;
    }

    /// <summary>The tables asked for: the one named, a sheet's, or every one.</summary>
    static List<TableFacts> Listed(ReferenceService references, ISwDocument doc, ISwDrawing drawing, TableReadRequest request)
    {
        if (request.Table != null && request.Sheet != null) throw ViewRequests.Invalid("/sheet", "/sheet: give table or sheet, not both (a table's reference or name@sheet already names its sheet)");
        if (request.Table != null) return new List<TableFacts> { references.ResolveTable(doc, drawing, request.Table, "/table") };
        string? sheet = request.Sheet == null ? null : references.ResolveSheet(doc, drawing, request.Sheet, "/sheet");
        return sheet == null ? DrawingTables.All(drawing) : drawing.Tables(sheet).ToList();
    }

    /// <summary>The drawing still has those tables with as many rows: nothing changed them while they were read.</summary>
    protected override void Verify(TableReadRequest request, TableReadResult result, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var now = Listed(_services.References, doc, drawing, request);
        bool same = now.Count == result.Tables.Count && now.Zip(result.Tables, (a, b) => a.Name == b.Name && a.Sheet == b.Sheet && a.Rows.Count == b.Rows.Count).All(x => x);
        if (!same)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the drawing's tables changed while they were read ({result.Tables.Count} tables, {result.Tables.Sum(t => t.Rows.Count)} rows; now {now.Count}, {now.Sum(t => t.Rows.Count)}); ask again",
                new JsonObject { ["check"] = "unchanged" });
        }
    }
}

public sealed class TableDeleteOperation : DrawingChangeOperation<TableDeleteRequest, TableDeleteResult>
{
    public TableDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    List<TableFacts> _deleted = new List<TableFacts>();
    List<string> _kept = new List<string>();

    protected override string? DocumentOf(TableDeleteRequest request) => request.Document;

    static string Named(TableFacts t) => t.Name + "@" + t.Sheet;

    protected override TableDeleteResult Change(ISwDocument doc, ISwDrawing drawing, TableDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Tables.Count == 0) throw ViewRequests.Invalid("/tables", "/tables: name at least one table to delete");
        _deleted = new List<TableFacts>();
        for (int i = 0; i < request.Tables.Count; i++)
        {
            var t = Table(doc, drawing, request.Tables[i], $"/tables/{i}");
            if (_deleted.Any(d => DrawingTables.Key(d) == DrawingTables.Key(t))) throw ViewRequests.Invalid($"/tables/{i}", $"/tables/{i}: {Named(t)} is named twice");
            _deleted.Add(t);
        }
        var keys = new HashSet<string>(_deleted.Select(DrawingTables.Key), StringComparer.Ordinal);
        _kept = DrawingTables.All(drawing).Select(DrawingTables.Key).Where(k => !keys.Contains(k)).ToList();
        foreach (var sheet in _deleted.GroupBy(t => t.Sheet))
        {
            if (!drawing.DeleteTables(sheet.Key, sheet.Select(t => t.Name).ToList()))
            {
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", sheet.Select(Named))}",
                    new JsonObject { ["tables"] = sheet.Select(t => (object?)Named(t)).ToList() });
            }
            // A deleted table cannot be made again as it was: a rollback after this says so (rollback_failed) for each that is gone.
            foreach (var t in sheet)
            {
                tx.Edited(new ChangedItem { Name = Named(t), Property = "deleted", Before = t.Bom ? "bom" : "table", After = "deleted" }, () =>
                {
                    if (DrawingTables.Read(drawing, t.Sheet, t.Name) == null)
                        throw new SwAgentException(ErrorCode.OperationFailed, $"{Named(t)} was deleted and cannot be put back");
                });
            }
        }
        context.Log.Info($"table.delete: {string.Join(", ", _deleted.Select(Named))}");
        return new TableDeleteResult { Deleted = _deleted.Select(Named).ToList() };
    }

    /// <summary>Each table deleted is gone and every other is still there.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, TableDeleteRequest request, TableDeleteResult result)
    {
        var now = new HashSet<string>(DrawingTables.All(drawing).Select(DrawingTables.Key), StringComparer.Ordinal);
        var left = _deleted.Where(t => now.Contains(DrawingTables.Key(t))).Select(Named).ToList();
        var lost = _kept.Where(k => !now.Contains(k)).Select(k => string.Join("@", k.Split('\n').Reverse())).ToList();
        if (left.Count == 0 && lost.Count == 0) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            left.Count > 0 ? $"SOLIDWORKS reported the tables deleted, but {string.Join(", ", left)} {(left.Count == 1 ? "is" : "are")} still there" : $"deleting went too far: {string.Join(", ", lost)} went too",
            new JsonObject { ["check"] = "tables", ["left"] = left.Select(l => (object?)l).ToList(), ["lost"] = lost.Select(l => (object?)l).ToList() });
    }
}

public sealed class TableBomOperation : DrawingChangeOperation<TableBomRequest, TableResult>
{
    public TableBomOperation(AddInServices services)
        : base(services)
    {
    }

    string _sheet = "";
    string _view = "";
    string _configuration = "";
    List<BomExpectation> _expected = new List<BomExpectation>();

    protected override string? DocumentOf(TableBomRequest request) => request.Document;

    protected override TableResult Change(ISwDocument doc, ISwDrawing drawing, TableBomRequest request, ModelTransaction tx, IOperationContext context)
    {
        _view = View(doc, drawing, request.View, "/view");
        var (facts, _, asm, configuration) = Tabling.Assembly(drawing, _view);
        _sheet = facts.Sheet;
        _configuration = configuration;
        if (request.At != null) ViewRequests.OnSheet(request.At, drawing.ReadSheet(_sheet)!, "/at");
        var kind = request.Kind ?? BomKind.TopLevel;
        // Every level, read as assembly.components reads it; BomRows takes the level asked.
        _expected = BomRows.Expected(asm.Components(topLevelOnly: false).Select(asm.Read).ToList(), kind);
        string name = drawing.InsertBom(_view, kind, configuration, request.At, request.Anchor ?? TableAnchor.TopLeft)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no bill of materials for {_view}", new JsonObject { ["view"] = _view, ["configuration"] = configuration });
        var made = DrawingTables.Read(drawing, _sheet, name)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS answered with the table {name}, but {_sheet} has no table of that name", new JsonObject { ["table"] = name });
        MadeTable(doc, drawing, tx, made);
        context.Log.Info($"table.bom: {name} on {_sheet} for {_view} ({configuration}, {WireNames.ForEnum(kind)}): {made.Rows.Count} rows; expected {string.Join(", ", _expected)}");
        return new TableResult { Table = DescribeTable(doc, made), Expected = _expected.Select(e => e.ToString()).ToList() };
    }

    /// <summary>Read back: a bill of materials of the view's configuration whose rows are exactly the ones counted.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, TableBomRequest request, TableResult result)
    {
        string name = result.Table.Name;
        var table = DrawingTables.Read(drawing, _sheet, name) ?? throw Tabling.Failed(name, "exists", $"on {_sheet}", "no such table");
        if (!table.Bom) throw Tabling.Failed(name, "kind", "a bill of materials", "another kind of table");
        if (table.Configuration != null && !string.Equals(table.Configuration, _configuration, StringComparison.OrdinalIgnoreCase))
            throw Tabling.Failed(name, "configuration", _configuration, table.Configuration);
        if (BomRows.Mismatch(_expected, table.Rows) is string wrong)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the table {name} failed its rows check: {wrong} (expected {string.Join(", ", _expected.DefaultIfEmpty(null).Select(e => e?.ToString() ?? "no rows"))}, read {string.Join(", ", table.Rows.Select(BomRows.Describe).DefaultIfEmpty("no rows"))})",
                new JsonObject
                {
                    ["check"] = "rows", ["table"] = name,
                    ["expected"] = _expected.Select(e => (object?)e.ToString()).ToList(),
                    ["measured"] = table.Rows.Select(r => (object?)BomRows.Describe(r)).ToList(),
                });
        }
        result.Table = DescribeTable(doc, table);
    }
}
