using System;
using System.Globalization;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class DimensionSetOperation : ModelChangeOperation<DimensionSetRequest, DimensionSetResult>
{
    public DimensionSetOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(DimensionSetRequest request) => request.Document;

    protected override DimensionSetResult Change(ISwDocument doc, DimensionSetRequest request, ModelTransaction tx, IOperationContext context)
    {
        var record = Dimensions.Find(Services.References, doc, request.Dimension, "dimension");
        if (!record.Driving)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"dimension: {record.FullName} is driven (it reports the geometry, it does not drive it), so it cannot be set",
                new JsonObject { ["field"] = "dimension", ["dimension"] = record.FullName });
        }
        double v = request.Value;
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0 || (record.Angle ? v >= 360 : v > SketchDraft.Reach))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                record.Angle ? $"value: {record.FullName} is an angle: more than 0 and less than 360 degrees" : $"value: {record.FullName} is a length: more than 0 and at most {SketchDraft.Reach} mm",
                new JsonObject { ["field"] = "value" });
        }
        double old = record.Value;
        tx.Edited(new ChangedItem { Name = record.FullName, Property = "value", Before = Dimensions.Text(old, record.Angle), After = Dimensions.Text(v, record.Angle) }, () =>
        {
            doc.SetDimension(record.Dimension, old);
            doc.Rebuild(false);
        });
        doc.SetDimension(record.Dimension, v);
        if (!doc.Rebuild(false))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS could not rebuild {doc.Info().Title} with {record.FullName} at {Dimensions.Text(v, record.Angle)}");
        var now = Dimensions.ByName(doc, record.FullName)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"{record.FullName} is gone after the rebuild");
        return new DimensionSetResult { Dimension = Sketches.Info(Services.References, doc, now) };
    }

    /// <summary>The dimension reads back at the value asked for.</summary>
    protected override void Check(ISwDocument doc, DimensionSetRequest request, DimensionSetResult result)
    {
        var now = Dimensions.ByName(doc, result.Dimension.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Dimension.Name} is not in the part after it was set");
        if (Math.Abs(now.Value - request.Value) > 1e-6 * Math.Max(1, Math.Abs(request.Value)))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{now.FullName} reads {Dimensions.Text(now.Value, now.Angle)}, not {Dimensions.Text(request.Value, now.Angle)}",
                new JsonObject { ["check"] = "dimension_value", ["expected"] = request.Value, ["measured"] = now.Value });
        }
    }
}

public sealed class ModelDimensionsOperation : Operation<ModelDimensionsRequest, ModelDimensionsResult>
{
    readonly AddInServices _services;

    public ModelDimensionsOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelDimensionsResult Run(ModelDimensionsRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.dimensions");
        object? feature = request.Feature == null ? null : _services.References.Resolve(doc, request.Feature, "feature", ReferenceKind.Feature, ReferenceKind.Sketch).Entity;
        return new ModelDimensionsResult
        {
            Document = doc.Info(),
            Dimensions = doc.Dimensions(feature).Select(d => Sketches.Info(_services.References, doc, d)).ToList(),
        };
    }

    /// <summary>Read again, the same dimensions are there.</summary>
    protected override void Verify(ModelDimensionsRequest request, ModelDimensionsResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        object? feature = request.Feature == null ? null : _services.References.Resolve(doc, request.Feature, "feature", ReferenceKind.Feature, ReferenceKind.Sketch).Entity;
        if (!doc.Dimensions(feature).Select(d => d.FullName).SequenceEqual(result.Dimensions.Select(d => d.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the dimensions changed while they were read; ask again");
    }
}

/// <summary>Finding a dimension by full name or reference, and writing its value with its unit.</summary>
public static class Dimensions
{
    public static DimensionRecord? ByName(ISwDocument doc, string fullName) =>
        doc.Dimensions(null).FirstOrDefault(d => string.Equals(d.FullName, fullName, StringComparison.OrdinalIgnoreCase));

    public static DimensionRecord Find(ReferenceService references, ISwDocument doc, string text, string field)
    {
        string name = text;
        if (SwAgent.Contracts.References.IsReference(text))
        {
            var (entity, _) = references.Resolve(doc, text, field, ReferenceKind.Dimension);
            name = doc.Describe(entity).Name ?? "";
        }
        return ByName(doc, name) ?? throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{field}: the part has no dimension named '{name}'; model.dimensions lists them (full names such as D1@Boss-Extrude1)",
            new JsonObject { ["field"] = field, ["dimension"] = name });
    }

    public static string Text(double value, bool angle) => string.Format(CultureInfo.InvariantCulture, "{0:0.######} {1}", value, angle ? "deg" : "mm");
}
