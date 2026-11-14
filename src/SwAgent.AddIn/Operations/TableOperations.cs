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
