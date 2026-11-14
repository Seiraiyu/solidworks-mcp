using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

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
