using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class DrawingPropertiesOperation : DrawingChangeOperation<DrawingPropertiesRequest, DrawingPropertiesResult>
{
    public DrawingPropertiesOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(DrawingPropertiesRequest request) => request.Document;

    protected override DrawingPropertiesResult Change(ISwDocument doc, ISwDrawing drawing, DrawingPropertiesRequest request, ModelTransaction tx, IOperationContext context)
    {
        var set = request.Set ?? new List<DrawingPropertyValue>();
        for (int i = 0; i < set.Count; i++)
        {
            string name = (set[i].Name ?? "").Trim();
            if (name.Length == 0) throw ViewRequests.Invalid($"/set/{i}/name", $"/set/{i}/name: a property needs a name");
            if (set.Take(i).Any(p => string.Equals((p.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)))
                throw ViewRequests.Invalid($"/set/{i}/name", $"/set/{i}/name: {name} is named twice");
            if (set[i].Value == null) throw ViewRequests.Invalid($"/set/{i}/value", $"/set/{i}/value: give a value (empty text clears it)");
        }
        var before = drawing.Properties();
        var written = new List<string>();
        foreach (var p in set)
        {
            string name = p.Name.Trim();
            var had = before.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!drawing.SetProperty(had?.Name ?? name, p.Value))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not write the property {name}", new JsonObject { ["property"] = name });
            written.Add(had?.Name ?? name);
            tx.Edited(new ChangedItem { Name = had?.Name ?? name, Property = "property", Before = had?.Value ?? "", After = p.Value }, () =>
            {
                bool back = had != null ? drawing.SetProperty(had.Name, had.Value) : drawing.DeleteProperty(name);
                if (!back) throw new SwAgentException(ErrorCode.OperationFailed, had != null ? $"SOLIDWORKS did not give {had.Name} its old value back" : $"SOLIDWORKS did not delete the new property {name}");
            });
        }
        context.Log.Info($"drawing.properties: {before.Count} properties; wrote {(written.Count == 0 ? "none" : string.Join(", ", written))}");
        return new DrawingPropertiesResult { Written = written };
    }

    /// <summary>Design 611, Title block: each property written reads back as written (Get2); the result gives every property, resolved.</summary>
    protected override void Check(ISwDocument doc, ISwDrawing drawing, DrawingPropertiesRequest request, DrawingPropertiesResult result)
    {
        var now = drawing.Properties();
        foreach (var p in request.Set ?? new List<DrawingPropertyValue>())
        {
            var read = now.FirstOrDefault(n => string.Equals(n.Name, p.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (read == null || !string.Equals(read.Value, p.Value, StringComparison.Ordinal))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the property {p.Name.Trim()} failed its value check: expected '{p.Value}', read back {(read == null ? "no such property" : $"'{read.Value}'")}",
                    new JsonObject { ["check"] = "value", ["property"] = p.Name.Trim(), ["expected"] = p.Value, ["measured"] = read?.Value });
            }
        }
        result.Properties = now.Select(n => new DrawingProperty { Name = n.Name, Value = n.Value, Resolved = n.Resolved }).ToList();
    }
}
