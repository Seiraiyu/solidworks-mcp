using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class SheetAddOperation : DrawingChangeOperation<SheetAddRequest, SheetResult>
{
    public SheetAddOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetAddRequest request) => request.Document;

    protected override SheetResult Change(ISwDocument doc, ISwDrawing drawing, SheetAddRequest request, ModelTransaction tx, IOperationContext context)
    {
        var setup = request.Setup();
        SheetSetups.Validate(setup, "");
        var names = drawing.SheetNames();
        if (request.Name != null && (request.Name.Trim().Length == 0 || names.Contains(request.Name, System.StringComparer.OrdinalIgnoreCase)))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                request.Name.Trim().Length == 0 ? "/name: a sheet's name cannot be empty" : $"/name: {doc.Info().Title} already has a sheet named {request.Name}",
                new JsonObject { ["field"] = "/name", ["sheets"] = names.Select(n => (object?)n).ToList() });
        }
        string name = request.Name ?? NextName(names);
        var first = drawing.ReadSheet(names[0]) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives nothing for the sheet {names[0]}");
        _wanted = SheetSetups.Apply(first, setup, name);
        if (!drawing.AddSheet(_wanted))
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS made no sheet ({SheetSetups.Describe(_wanted)}); a sheet format that does not fit the size is the likely reason",
                new JsonObject { ["sheet"] = name });
        }
        string made = drawing.SheetNames().Except(names).FirstOrDefault()
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS reported the sheet {name} made, but the drawing has no new sheet");
        MadeSheet(doc, drawing, tx, made);
        context.Log.Info($"sheet.add: {made}, {SheetSetups.Describe(_wanted)}");
        return new SheetResult { Sheet = new SheetInfo { Name = made } };
    }

    SheetFacts? _wanted;

    /// <summary>One more sheet, reading back as asked.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, SheetAddRequest request, SheetResult result)
    {
        string name = result.Sheet.Name;
        var actual = drawing.ReadSheet(name) ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the sheet {name} is not in the drawing", new JsonObject { ["check"] = "exists" });
        if (SheetSetups.Mismatch(_wanted!, actual) is { } mismatch) throw SheetSetups.NotAsAsked(name, mismatch);
        result.Sheet = DescribeSheet(doc, drawing, name);
    }

    /// <summary>SOLIDWORKS' own way of naming sheets: Sheet and the first number no sheet has.</summary>
    static string NextName(System.Collections.Generic.IReadOnlyList<string> names)
    {
        int n = names.Count + 1;
        while (names.Contains("Sheet" + n, System.StringComparer.OrdinalIgnoreCase)) n++;
        return "Sheet" + n;
    }
}

public sealed class SheetSetOperation : DrawingChangeOperation<SheetSetRequest, SheetResult>
{
    public SheetSetOperation(AddInServices services)
        : base(services)
    {
    }

    SheetFacts? _wanted;
    SheetFacts? _old;

    protected override string? DocumentOf(SheetSetRequest request) => request.Document;

    protected override SheetResult Change(ISwDocument doc, ISwDrawing drawing, SheetSetRequest request, ModelTransaction tx, IOperationContext context)
    {
        string sheet = Sheet(doc, drawing, request.Sheet, "/sheet");
        var setup = request.Setup();
        SheetSetups.Validate(setup, "");
        if (request.Name == null && SheetSizes.Asked(setup, "") == null && setup.Scale == null && setup.Projection == null && setup.Format == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                "sheet.set changes nothing: give name, size (or width_mm and height_mm), scale, projection or format",
                new JsonObject { ["field"] = "/sheet" });
        }
        var names = drawing.SheetNames();
        if (request.Name != null && (request.Name.Trim().Length == 0 || names.Any(n => n != sheet && string.Equals(n, request.Name, System.StringComparison.OrdinalIgnoreCase))))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                request.Name.Trim().Length == 0 ? "/name: a sheet's name cannot be empty" : $"/name: {doc.Info().Title} already has a sheet named {request.Name}",
                new JsonObject { ["field"] = "/name", ["sheets"] = names.Select(n => (object?)n).ToList() });
        }
        var old = drawing.ReadSheet(sheet)!;
        var wanted = SheetSetups.Apply(old, setup, request.Name);
        _old = old;
        _wanted = wanted;
        drawing.SetSheet(sheet, wanted);
        string changed = string.Join(", ", Properties(old, wanted).DefaultIfEmpty("nothing"));
        tx.Edited(new ChangedItem { Name = sheet, Property = changed, Before = Describe(old), After = Describe(wanted) }, () =>
        {
            drawing.SetSheet(wanted.Name, old);
            if (!(drawing.ReadSheet(old.Name) is SheetFacts back) || SheetSetups.Mismatch(old, back) != null)
                throw new SwAgentException(ErrorCode.OperationFailed, $"the sheet {old.Name} did not take its old values back ({SheetSetups.Describe(old)})");
        });
        context.Log.Info($"sheet.set: {sheet}: {changed}: {SheetSetups.Describe(old)} -> {SheetSetups.Describe(wanted)}");
        return new SheetResult { Sheet = new SheetInfo { Name = wanted.Name } };
    }

    /// <summary>Every value reads back, and each view that follows the sheet's scale is drawn at it.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, SheetSetRequest request, SheetResult result)
    {
        string name = result.Sheet.Name;
        var actual = drawing.ReadSheet(name) ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"no sheet is named {name} after the change", new JsonObject { ["check"] = "name" });
        if (SheetSetups.Mismatch(_wanted!, actual) is { } mismatch) throw SheetSetups.NotAsAsked(name, mismatch);
        if (!DrawingReader.SameScale(_old!.Scale, actual.Scale))
        {
            foreach (var view in drawing.ViewNames(name))
            {
                if (drawing.ReadView(view) is ViewFacts facts && facts.SheetScale) ViewChecks.Drawn(drawing, view, DrawingReader.Ratio(actual.Scale), null);
            }
        }
        result.Sheet = DescribeSheet(doc, drawing, name);
    }

    static System.Collections.Generic.IEnumerable<string> Properties(SheetFacts a, SheetFacts b)
    {
        if (a.Name != b.Name) yield return "name";
        if (System.Math.Abs(a.WidthMm - b.WidthMm) > SheetSizes.ToleranceMm || System.Math.Abs(a.HeightMm - b.HeightMm) > SheetSizes.ToleranceMm) yield return "size";
        if (!DrawingReader.SameScale(a.Scale, b.Scale)) yield return "scale";
        if (a.Projection != b.Projection) yield return "projection";
        if (!string.Equals(a.Format, b.Format, System.StringComparison.OrdinalIgnoreCase)) yield return "format";
    }

    static string Describe(SheetFacts s) => $"{s.Name}: {SheetSetups.Describe(s)}";
}

public sealed class SheetActivateOperation : Operation<SheetActivateRequest, SheetActivateResult>
{
    readonly AddInServices _services;

    public SheetActivateOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SheetActivateResult Run(SheetActivateRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        DrawingChangeGuard.Require(_services.SolidWorks, doc, drawing, Name);
        string sheet = _services.References.ResolveSheet(doc, drawing, request.Sheet, "/sheet");
        string? before = drawing.ActiveSheet();
        if (!drawing.ActivateSheet(sheet))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not show the sheet {sheet}", new JsonObject { ["sheet"] = sheet });
        context.Log.Info($"sheet.activate: {sheet} (was {before ?? "none"})");
        return new SheetActivateResult { Document = doc.Info(), Sheet = sheet, Before = before };
    }

    protected override void Verify(SheetActivateRequest request, SheetActivateResult result, IOperationContext context)
    {
        var (_, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        string? now = drawing.ActiveSheet();
        if (now != result.Sheet)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS shows {now ?? "no sheet"}, not {result.Sheet}",
                new JsonObject { ["check"] = "active", ["expected"] = result.Sheet, ["measured"] = now });
        }
    }
}

public sealed class DrawingReadOperation : Operation<DrawingReadRequest, DrawingReadResult>
{
    readonly AddInServices _services;

    public DrawingReadOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DrawingReadResult Run(DrawingReadRequest request, IOperationContext context)
    {
        var (doc, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, request.Document, Name);
        var result = new DrawingReadResult { Document = doc.Info(), ActiveSheet = drawing.ActiveSheet() };
        foreach (var name in drawing.SheetNames())
        {
            if (drawing.ReadSheet(name) is SheetFacts facts) result.Sheets.Add(DrawingReader.Sheet(_services.References, doc, drawing, facts, withViews: true));
        }
        var open = _services.SolidWorks.Documents().Select(d => d.Info()).Where(i => i.Path != null).ToList();
        foreach (var path in doc.UsedFiles())
        {
            var model = open.FirstOrDefault(i => DocumentKeys.Same(Path.GetFullPath(i.Path!), Path.GetFullPath(path)));
            result.Models.Add(new DrawingModel { Path = path, Loaded = model != null, Dirty = model?.Dirty == true, Missing = !File.Exists(path) });
        }
        context.Log.Info($"drawing.read: {DocumentLocator.Label(result.Document)}, {result.Sheets.Count} sheets, {result.Sheets.Sum(s => s.Views.Count)} views, {result.Models.Count} models");
        return result;
    }

    /// <summary>The sheets and views read are still there: nothing changed them while they were read.</summary>
    protected override void Verify(DrawingReadRequest request, DrawingReadResult result, IOperationContext context)
    {
        var (_, drawing) = DocumentLocator.FindDrawing(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        var sheets = drawing.SheetNames();
        var views = sheets.SelectMany(drawing.ViewNames).ToList();
        if (!sheets.SequenceEqual(result.Sheets.Select(s => s.Name)) || !views.SequenceEqual(result.Sheets.SelectMany(s => s.Views).Select(v => v.Name)))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the drawing's sheets or views changed while it was read ({result.Sheets.Count} sheets and {result.Sheets.Sum(s => s.Views.Count)} views, now {sheets.Count} and {views.Count}); ask again",
                new JsonObject { ["check"] = "unchanged" });
        }
    }
}
