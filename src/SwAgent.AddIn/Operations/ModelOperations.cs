using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class ModelTreeOperation : Operation<ModelTreeRequest, ModelTreeResult>
{
    readonly AddInServices _services;

    public ModelTreeOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelTreeResult Run(ModelTreeRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.tree");
        var nodes = doc.Features().Select(f =>
        {
            var info = _services.References.Describe(doc, f.Feature);
            return new FeatureNode
            {
                Name = f.Name,
                Type = f.Type,
                Kind = info.Kind,
                Reference = info.Reference,
                Parent = f.Parent,
                Suppressed = f.Suppressed,
                Error = f.ErrorCode == 0 ? null : SolidWorksCodes.FeatureError(f.ErrorCode),
                Warning = f.ErrorCode != 0 && f.Warning,
            };
        }).ToList();
        return new ModelTreeResult { Document = doc.Info(), Features = nodes };
    }

    /// <summary>The tree read again has the same features in the same order.</summary>
    protected override void Verify(ModelTreeRequest request, ModelTreeResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var names = doc.Features().Select(f => f.Name).ToList();
        if (!names.SequenceEqual(result.Features.Select(f => f.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the feature tree changed while it was read; ask again");
    }
}

public sealed class ModelMassOperation : Operation<ModelMassRequest, ModelMassResult>
{
    readonly AddInServices _services;

    public ModelMassOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelMassResult Run(ModelMassRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.mass");
        int bodies = doc.SolidBodyCount();
        var mass = bodies == 0 ? null : doc.Mass();
        var result = mass ?? new ModelMassResult();
        result.Document = doc.Info();
        result.Bodies = bodies;
        result.Material = doc.Material();
        result.PrincipalMomentsGMm2 = result.PrincipalMomentsGMm2.OrderBy(m => m).ToList();
        if (bodies > 0 && mass == null)
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS gave no mass properties for {result.Document.Title}, which has {bodies} solid bodies");
        return result;
    }

    /// <summary>Mass is density times volume, and the volume read again is the same.</summary>
    protected override void Verify(ModelMassRequest request, ModelMassResult result, IOperationContext context)
    {
        if (result.Bodies == 0) return;
        double expected = result.DensityKgM3 * result.VolumeMm3 * 1e-6;
        if (Math.Abs(result.MassG - expected) > 1e-6 * Math.Max(1, expected))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS reported a mass of {result.MassG} g, but density × volume is {expected} g");
        }
        var again = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document)).Mass();
        if (again == null || Math.Abs(again.VolumeMm3 - result.VolumeMm3) > 1e-9 * Math.Max(1, result.VolumeMm3))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the volume changed while it was read; ask again");
    }
}

public sealed class ModelBoundingBoxOperation : Operation<ModelBoundingBoxRequest, ModelBoundingBoxResult>
{
    readonly AddInServices _services;

    public ModelBoundingBoxOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelBoundingBoxResult Run(ModelBoundingBoxRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.bounding_box");
        bool precise = request.Precise == true;
        int bodies = doc.SolidBodyCount();
        var box = bodies == 0 ? null : doc.BoundingBox(precise);
        return new ModelBoundingBoxResult
        {
            Document = doc.Info(),
            Bodies = bodies,
            Box = box,
            SizeX = box == null ? 0 : box.Max.X - box.Min.X,
            SizeY = box == null ? 0 : box.Max.Y - box.Min.Y,
            SizeZ = box == null ? 0 : box.Max.Z - box.Min.Z,
            Precise = precise,
        };
    }

    /// <summary>A part with bodies has a box, and its corners are in order.</summary>
    protected override void Verify(ModelBoundingBoxRequest request, ModelBoundingBoxResult result, IOperationContext context)
    {
        if (result.Bodies > 0 && result.Box == null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS gave no bounding box for a part with {result.Bodies} solid bodies");
        if (result.Box != null && (result.SizeX < 0 || result.SizeY < 0 || result.SizeZ < 0))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS gave a box whose corners are out of order: {result.Box.Min} to {result.Box.Max}");
    }
}

public sealed class ModelFindOperation : Operation<ModelFindRequest, ModelFindResult>
{
    readonly AddInServices _services;

    public ModelFindOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelFindResult Run(ModelFindRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.find");
        var query = Query(doc, request);
        var candidates = doc.Topology(request.Kind).Select(entity =>
        {
            var info = doc.Describe(entity);
            if (query.Near != null && (query.Feature == null || string.Equals(info.Feature, query.Feature, StringComparison.OrdinalIgnoreCase)))
                info.DistanceMm = doc.DistanceMm(entity, query.Near);
            return (Entity: entity, Info: info);
        }).ToList();
        var (kept, total) = EntityFilter.Apply(candidates, c => c.Info, query);
        foreach (var c in kept) c.Info.Reference = _services.References.Issue(doc, c.Entity, c.Info);
        return new ModelFindResult
        {
            Document = doc.Info(),
            Entities = kept.Select(c => c.Info).ToList(),
            Total = total,
            Truncated = total > kept.Count,
        };
    }

    /// <summary>Everything returned matches the request, within its limit.</summary>
    protected override void Verify(ModelFindRequest request, ModelFindResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var query = Query(doc, request);
        var wrong = result.Entities.FirstOrDefault(e => !EntityFilter.Matches(e, query) || e.Kind != Kind(request.Kind));
        if (wrong != null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"model.find returned the {EntityFilter.Describe(wrong)}, which does not match the request");
        if (result.Entities.Count > query.Limit)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"model.find returned {result.Entities.Count} entities, more than its limit of {query.Limit}");
    }

    static ReferenceKind Kind(TopologyKind kind) => kind switch
    {
        TopologyKind.Edge => ReferenceKind.Edge,
        TopologyKind.Vertex => ReferenceKind.Vertex,
        _ => ReferenceKind.Face,
    };

    EntityQuery Query(ISwDocument doc, ModelFindRequest request)
    {
        var query = new EntityQuery { Geometry = request.Geometry, Direction = request.Direction, Near = request.Near };
        if (request.Feature != null)
        {
            var (feature, _) = _services.References.Resolve(doc, request.Feature, "feature", ReferenceKind.Feature);
            query.Feature = doc.Describe(feature).Name;
        }
        if (request.Direction != null && request.Direction.X == 0 && request.Direction.Y == 0 && request.Direction.Z == 0)
            throw Invalid("direction", "direction must not be zero");
        if (request.AngleToleranceDeg != null)
        {
            if (request.AngleToleranceDeg < 0 || request.AngleToleranceDeg > 90) throw Invalid("angle_tolerance_deg", "angle_tolerance_deg must be from 0 to 90");
            if (request.Direction == null) throw Invalid("angle_tolerance_deg", "angle_tolerance_deg needs direction");
            query.AngleToleranceDeg = request.AngleToleranceDeg.Value;
        }
        if (request.MaxDistanceMm != null)
        {
            if (request.MaxDistanceMm <= 0) throw Invalid("max_distance_mm", "max_distance_mm must be more than 0");
            if (request.Near == null) throw Invalid("max_distance_mm", "max_distance_mm needs near");
            query.MaxDistanceMm = request.MaxDistanceMm.Value;
        }
        if (request.Limit != null)
        {
            if (request.Limit < 1 || request.Limit > EntityQuery.MaxLimit) throw Invalid("limit", $"limit must be from 1 to {EntityQuery.MaxLimit}");
            query.Limit = request.Limit.Value;
        }
        return query;
    }

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new SwAgent.Contracts.Serialization.JsonObject { ["field"] = field });
}
