using System;
using System.Collections.Generic;
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
        var doc = DocumentLocator.FindModel(_services.SolidWorks, request.Document, "model.mass");
        int bodies = doc.SolidBodyCount();
        var mass = bodies == 0 ? null : doc.Mass();
        var result = mass ?? new ModelMassResult();
        result.Document = doc.Info();
        result.Bodies = bodies;
        result.Material = doc.Material();
        result.PrincipalMomentsGMm2 = result.PrincipalMomentsGMm2.OrderBy(m => m).ToList();
        if (bodies > 0 && mass == null)
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS gave no mass properties for {result.Document.Title}, which has {bodies} solid bodies");
        if (doc.Assembly() is ISwAssembly asm)
        {
            result.Material = null;
            result.DensityKgM3 = result.VolumeMm3 > 0 ? result.MassG / result.VolumeMm3 * 1e6 : 0;
            result.Components = Shares(asm);
        }
        return result;
    }

    /// <summary>Each top-level component's mass properties, but the suppressed and missing ones, which have no mass in the assembly.</summary>
    static List<ComponentMass> Shares(ISwAssembly asm)
    {
        var shares = new List<ComponentMass>();
        foreach (var component in asm.Components(topLevelOnly: true))
        {
            var who = asm.Identify(component);
            if (who.State == ComponentState.Suppressed || who.State == ComponentState.Missing) continue;
            var mass = asm.Mass(component)
                ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS gave no mass properties for the component {who.Name}",
                    new SwAgent.Contracts.Serialization.JsonObject { ["component"] = who.Name });
            shares.Add(new ComponentMass { Name = who.Name, VolumeMm3 = mass.VolumeMm3, MassG = mass.MassG, CenterOfMass = mass.MassG > 0 ? mass.CenterOfMass : null });
        }
        return shares;
    }

    /// <summary>
    /// A part's mass is density times volume; an assembly's components' masses and volumes add up to its own. Either way
    /// the volume read again is the same.
    /// </summary>
    protected override void Verify(ModelMassRequest request, ModelMassResult result, IOperationContext context)
    {
        if (result.Bodies == 0) return;
        if (result.Components != null)
        {
            double sum = result.Components.Sum(c => c.MassG), volume = result.Components.Sum(c => c.VolumeMm3);
            if (Math.Abs(sum - result.MassG) > 1e-6 * Math.Max(1, result.MassG) || Math.Abs(volume - result.VolumeMm3) > 1e-6 * Math.Max(1, result.VolumeMm3))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the components' masses add up to {sum} g and {volume} mm³, but SOLIDWORKS reported {result.MassG} g and {result.VolumeMm3} mm³ for {result.Document.Title}",
                    new SwAgent.Contracts.Serialization.JsonObject { ["check"] = "components_sum", ["expected"] = result.MassG, ["measured"] = sum });
            }
        }
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
        var doc = DocumentLocator.FindModel(_services.SolidWorks, request.Document, "model.bounding_box");
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

    /// <summary>A part or assembly with bodies has a box, and its corners are in order.</summary>
    protected override void Verify(ModelBoundingBoxRequest request, ModelBoundingBoxResult result, IOperationContext context)
    {
        if (result.Bodies > 0 && result.Box == null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS gave no bounding box for {result.Document.Title}, which has {result.Bodies} solid bodies");
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
        var doc = DocumentLocator.FindModel(_services.SolidWorks, request.Document, "model.find");
        var (query, scope) = Query(doc, request);
        var skipped = new List<SkippedComponent>();
        var candidates = new List<(object Entity, EntityInfo Info)>();
        if (doc.Assembly() is ISwAssembly asm)
        {
            foreach (var component in Searched(asm, scope))
            {
                var who = asm.Identify(component);
                if (who.State != ComponentState.Resolved)
                {
                    skipped.Add(new SkippedComponent { Name = who.Name, State = who.State });
                    continue;
                }
                foreach (var entity in asm.Topology(component, request.Kind))
                {
                    var info = asm.Describe(component, entity);
                    if (query.Near != null && (query.Feature == null || string.Equals(info.Feature, query.Feature, StringComparison.OrdinalIgnoreCase)))
                        info.DistanceMm = asm.DistanceMm(component, entity, query.Near);
                    candidates.Add((entity, info));
                }
            }
        }
        else
        {
            candidates.AddRange(doc.Topology(request.Kind).Select(entity =>
            {
                var info = doc.Describe(entity);
                if (query.Near != null && (query.Feature == null || string.Equals(info.Feature, query.Feature, StringComparison.OrdinalIgnoreCase)))
                    info.DistanceMm = doc.DistanceMm(entity, query.Near);
                return (entity, info);
            }));
        }
        var (kept, total) = EntityFilter.Apply(candidates, c => c.Info, query);
        foreach (var c in kept) c.Info.Reference = _services.References.Issue(doc, c.Entity, c.Info);
        return new ModelFindResult
        {
            Document = doc.Info(),
            Skipped = skipped,
            Entities = kept.Select(c => c.Info).ToList(),
            Total = total,
            Truncated = total > kept.Count,
        };
    }

    /// <summary>
    /// The part components an assembly search covers, and every subassembly in it that is not loaded (so it is listed as
    /// skipped): the whole tree, or <paramref name="scope"/> and what is inside it. A loaded subassembly has no bodies of
    /// its own; its components are searched in its place.
    /// </summary>
    static IEnumerable<object> Searched(ISwAssembly asm, ComponentIdentity? scope)
    {
        foreach (var component in asm.Components(false))
        {
            var who = asm.Identify(component);
            if (scope != null && !string.Equals(who.Name, scope.Name, StringComparison.OrdinalIgnoreCase)
                && !who.Name.StartsWith(scope.Name + "/", StringComparison.OrdinalIgnoreCase)) continue;
            bool subassembly = who.Path != null && who.Path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
            if (subassembly && who.State == ComponentState.Resolved) continue;
            yield return component;
        }
    }

    /// <summary>Everything returned matches the request, within its limit; in an assembly, belongs to a component the search covered.</summary>
    protected override void Verify(ModelFindRequest request, ModelFindResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var (query, scope) = Query(doc, request);
        var wrong = result.Entities.FirstOrDefault(e => !EntityFilter.Matches(e, query) || e.Kind != Kind(request.Kind)
            || doc.Assembly() != null && (e.Component == null || scope != null && !Within(e.Component, scope.Name)));
        if (wrong != null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"model.find returned the {EntityFilter.Describe(wrong)}, which does not match the request");
        if (result.Entities.Count > query.Limit)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"model.find returned {result.Entities.Count} entities, more than its limit of {query.Limit}");
    }

    static bool Within(string component, string scope) =>
        string.Equals(component, scope, StringComparison.OrdinalIgnoreCase) || component.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase);

    static ReferenceKind Kind(TopologyKind kind) => kind switch
    {
        TopologyKind.Edge => ReferenceKind.Edge,
        TopologyKind.Vertex => ReferenceKind.Vertex,
        _ => ReferenceKind.Face,
    };

    /// <summary>The validated query, and in an assembly the component the search is limited to (from component, or the feature's own component).</summary>
    (EntityQuery Query, ComponentIdentity? Scope) Query(ISwDocument doc, ModelFindRequest request)
    {
        var query = new EntityQuery { Geometry = request.Geometry, Direction = request.Direction, Near = request.Near };
        var asm = doc.Assembly();
        ComponentIdentity? scope = null;
        if (request.Component != null)
        {
            if (asm == null) throw Invalid("component", $"component names a component, which only an assembly has; {doc.Info().Title} is a part, so leave it out");
            scope = asm.Identify(ComponentLookup.Find(_services.References, doc, asm, request.Component, "component"));
        }
        if (request.Feature != null)
        {
            var (feature, _) = _services.References.Resolve(doc, request.Feature, "feature", ReferenceKind.Feature);
            var info = _services.References.Describe(doc, feature);
            query.Feature = info.Name;
            if (asm != null && info.Component != null)
            {
                if (scope != null && !Within(info.Component, scope.Name))
                    throw Invalid("feature", $"feature: {request.Feature} is a feature of {info.Component}, which is not in the component searched ({scope.Name})");
                scope = asm.Identify(_services.References.ComponentOf(doc, feature) ?? asm.ComponentByName(info.Component)!);
            }
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
        return (query, scope);
    }

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new SwAgent.Contracts.Serialization.JsonObject { ["field"] = field });
}

public sealed class ModelMeasureOperation : Operation<ModelMeasureRequest, ModelMeasureResult>
{
    static readonly ReferenceKind[] Measurable = { ReferenceKind.Face, ReferenceKind.Edge, ReferenceKind.Vertex, ReferenceKind.Plane, ReferenceKind.Axis, ReferenceKind.Point };

    readonly AddInServices _services;

    public ModelMeasureOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelMeasureResult Run(ModelMeasureRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindModel(_services.SolidWorks, request.Document, "model.measure");
        return Measure(doc, request);
    }

    /// <summary>An entity named in the document itself, or (with its component field) in a component's own part.</summary>
    object Entity(ISwDocument doc, string text, string? component, string field) =>
        component == null
            ? _services.References.Resolve(doc, text, field, Measurable).Entity
            : _services.References.ResolveInComponent(doc, text, component, field, field + "_component", Measurable).Entity;

    ModelMeasureResult Measure(ISwDocument doc, ModelMeasureRequest request)
    {
        var a = Entity(doc, request.A, request.AComponent, "a");
        var b = Entity(doc, request.B, request.BComponent, "b");
        var infoA = _services.References.Describe(doc, a);
        var infoB = _services.References.Describe(doc, b);
        double distance = doc.ClosestDistance(a, b, out var onA, out var onB);
        var result = new ModelMeasureResult { Document = doc.Info(), A = infoA, B = infoB, DistanceMm = distance, PointA = onA, PointB = onB };
        var ca = Measures.Centre(infoA);
        var cb = Measures.Centre(infoB);
        if (ca != null && cb != null) result.CenterDistanceMm = Math.Sqrt(Math.Pow(ca.X - cb.X, 2) + Math.Pow(ca.Y - cb.Y, 2) + Math.Pow(ca.Z - cb.Z, 2));
        var da = Measures.Direction(infoA);
        var db = Measures.Direction(infoB);
        if (da != null && db != null) result.AngleDeg = FeatureChecks.AngleDeg(da, db);
        return result;
    }

    /// <summary>Measured again, the distance is the same.</summary>
    protected override void Verify(ModelMeasureRequest request, ModelMeasureResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var again = Measure(doc, request);
        if (Math.Abs(again.DistanceMm - result.DistanceMm) > 1e-9 * Math.Max(1, result.DistanceMm))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the distance read {result.DistanceMm} mm and then {again.DistanceMm} mm; {result.Document.Title} changed while it was measured, so ask again",
                new SwAgent.Contracts.Serialization.JsonObject { ["check"] = "distance", ["first"] = result.DistanceMm, ["second"] = again.DistanceMm });
    }
}

/// <summary>What model.measure reads from a description: a centre, and a direction.</summary>
public static class Measures
{
    /// <summary>The centre of a circle or sphere, or a vertex or point itself; null for anything else.</summary>
    public static Point3? Centre(EntityInfo e) =>
        e.Geometry == GeometryType.Circle || e.Geometry == GeometryType.Sphere || e.Geometry == GeometryType.Point ? e.Origin : null;

    /// <summary>A plane's or planar face's normal, or the axis of a straight edge, axis, cylinder or cone; null for anything else.</summary>
    public static Vector3? Direction(EntityInfo e) => e.Geometry switch
    {
        GeometryType.Plane => e.Normal,
        GeometryType.Line or GeometryType.Cylinder or GeometryType.Cone => e.Axis,
        _ => null,
    };
}