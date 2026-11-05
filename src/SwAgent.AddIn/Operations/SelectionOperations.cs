using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// Reads a part's selection as selection.get, diag.select and selection.changed report it: the count, and at most a
/// cap of items described, since describing each one costs SOLIDWORKS' UI thread (a box select can pick thousands).
/// </summary>
public static class SelectionReading
{
    /// <summary>How many items selection.get and diag.select describe.</summary>
    public const int MaxRead = 1000;

    /// <summary>How many items a selection.changed event describes.</summary>
    public const int MaxEvent = 100;

    public static (int Total, List<SelectedItem> Items) Read(ISwDocument doc, ReferenceService references, int max) =>
        (doc.SelectionCount(), doc.Selection(max).Select(s => new SelectedItem
        {
            Type = SolidWorksCodes.SelectType(s.Type),
            Entity = s.Entity != null && doc.KindOf(s.Entity) != null ? references.Describe(doc, s.Entity) : null,
        }).ToList());

    public static SelectionResult Result(ISwDocument doc, ReferenceService references)
    {
        var (total, items) = Read(doc, references, MaxRead);
        return new SelectionResult { Document = doc.Info(), Total = total, Items = items };
    }

    public static SelectionChangedEvent Event(ISwDocument doc, ReferenceService references)
    {
        var info = doc.Info();
        var (total, items) = Read(doc, references, MaxEvent);
        return new SelectionChangedEvent { Title = info.Title, Path = info.Path, Total = total, Items = items };
    }
}

public sealed class SelectionGetOperation : Operation<SelectionGetRequest, SelectionResult>
{
    readonly AddInServices _services;

    public SelectionGetOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SelectionResult Run(SelectionGetRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "selection.get");
        return SelectionReading.Result(doc, _services.References);
    }

    /// <summary>The selection read again has as many items, and no more were described than the cap.</summary>
    protected override void Verify(SelectionGetRequest request, SelectionResult result, IOperationContext context)
    {
        int now = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document)).SelectionCount();
        if (now != result.Total)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the selection changed while it was read ({result.Total} items, now {now}); ask again");
        if (result.Items.Count != Math.Min(result.Total, SelectionReading.MaxRead))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Items.Count} of {result.Total} selected items were described");
    }
}

public sealed class DiagSelectOperation : Operation<DiagSelectRequest, SelectionResult>
{
    readonly AddInServices _services;

    public DiagSelectOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SelectionResult Run(DiagSelectRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (request.Items.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "items must name at least one entity", new JsonObject { ["field"] = "items" });
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "diag.select");
        var entities = request.Items.Select((item, i) => _services.References.Resolve(doc, item, $"items/{i}").Entity).ToList();
        using (_services.Runtime.Selection?.AllowDuringOperation())
        {
            if (request.Append != true) doc.ClearSelection();
            for (int i = 0; i < entities.Count; i++)
            {
                if (!doc.Select(entities[i], append: true))
                {
                    throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not select items/{i} ({request.Items[i]})",
                        new JsonObject { ["field"] = $"items/{i}" });
                }
            }
            _services.Runtime.Selection?.Changed(() => SelectionReading.Event(doc, _services.References));
            return SelectionReading.Result(doc, _services.References);
        }
    }

    /// <summary>Everything asked for is selected.</summary>
    protected override void Verify(DiagSelectRequest request, SelectionResult result, IOperationContext context)
    {
        if (request.Append != true && result.Total != request.Items.Count)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{request.Items.Count} items were selected but SOLIDWORKS reports {result.Total}");
    }
}
