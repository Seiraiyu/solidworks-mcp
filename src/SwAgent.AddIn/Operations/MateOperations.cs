using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the mate operations check of a request before anything is asked of SOLIDWORKS (design 455, Mates).</summary>
public static class MateRequests
{
    public static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });

    public static void Validate(MateType type, double? distanceMm, double? angleDeg, MateAlignment? alignment, bool? flip)
    {
        string kind = WireNames.ForEnum(type);
        if (type == MateType.Distance && distanceMm == null) throw Invalid("/distance_mm", "a distance mate needs distance_mm");
        if (type != MateType.Distance && distanceMm != null) throw Invalid("/distance_mm", $"distance_mm is for distance mates, not {kind}");
        if (distanceMm is double d && !(d >= 0 && d <= 100_000)) throw Invalid("/distance_mm", $"distance_mm must be 0 to 100000 mm, not {d}");
        if (type == MateType.Angle && angleDeg == null) throw Invalid("/angle_deg", "an angle mate needs angle_deg");
        if (type != MateType.Angle && angleDeg != null) throw Invalid("/angle_deg", $"angle_deg is for angle mates, not {kind}");
        if (angleDeg is double a && !(a >= 0 && a <= 180)) throw Invalid("/angle_deg", $"angle_deg must be 0 to 180 degrees, not {a}");
        if (alignment != null && (type == MateType.Lock || type == MateType.Perpendicular)) throw Invalid("/alignment", $"a {kind} mate has no alignment");
        if (flip == true && type != MateType.Distance && type != MateType.Angle) throw Invalid("/flip", "flip is for distance and angle mates: it puts the value on the other side");
    }
}

/// <summary>Describing and measuring mates, for every mate operation.</summary>
public static class MateChecks
{
    /// <summary>A side read again after the solve: resolved afresh from its reference when it has one, and described as it is now.</summary>
    public static (object Entity, EntityInfo Info) Again(ReferenceService references, ISwDocument doc, ResolvedSide side, string field)
    {
        if (side.Info.Reference == null) return (side.Entity, references.Describe(doc, side.Entity));
        var entity = references.Resolve(doc, side.Info.Reference, field).Entity;
        return (entity, references.Describe(doc, entity));
    }

    public static List<MateMeasurement> Measure(ISwDocument doc, MateType type, (object Entity, EntityInfo Info) a, (object Entity, EntityInfo Info) b, double? distanceMm, double? angleDeg) =>
        MateGeometry.Measure(type, a.Info, b.Info, distanceMm, angleDeg, () => doc.ClosestDistance(a.Entity, b.Entity, out _, out _)).ToList();

    /// <summary>Throws verification_failed with the measured condition when one does not hold.</summary>
    public static void Require(IReadOnlyList<MateMeasurement> measured, string mate)
    {
        var failing = MateGeometry.Failing(measured);
        if (failing == null) return;
        throw new SwAgentException(ErrorCode.VerificationFailed, $"{mate} does not hold: {MateGeometry.Say(failing)}",
            new JsonObject { ["check"] = failing.Condition, ["expected"] = failing.Expected, ["measured"] = failing.Measured, ["tolerance"] = failing.Tolerance });
    }

    public static MateInfo Describe(ReferenceService references, ISwDocument doc, ISwAssembly asm, object mate)
    {
        var facts = asm.ReadMate(mate);
        var info = new MateInfo
        {
            Name = facts.Name, Type = facts.Type, SolidWorksType = facts.SolidWorksType, Alignment = facts.Alignment, Flipped = facts.Flipped,
            DistanceMm = facts.DistanceMm, AngleDeg = facts.AngleDeg, Suppressed = facts.Suppressed, Error = facts.Error,
        };
        info.Reference = references.Issue(doc, mate, doc.Describe(mate));
        foreach (var (component, entity) in facts.Entities)
        {
            EntityInfo side;
            try
            {
                side = references.Describe(doc, entity);
            }
            catch (SwAgentException)
            {
                side = new EntityInfo { Geometry = GeometryType.Other };
            }
            if (component != null) side.Component ??= asm.Identify(component).Name;
            info.Entities.Add(side);
        }
        return info;
    }
}

/// <summary>mate.add (design 455, Mates): checked by measuring the condition the mate imposes, rolled back with every moved component put back.</summary>
public sealed class MateAddOperation : AssemblyChangeOperation<MateAddRequest, MateAddResult>
{
    // One request runs at a time: the sides and, for lock, where the two components were.
    ResolvedSide? _a, _b;
    Transform? _lockA, _lockB;

    public MateAddOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(MateAddRequest request) => request.Document;

    protected override MateAddResult Change(ISwDocument doc, ISwAssembly asm, MateAddRequest request, ModelTransaction tx, IOperationContext context)
    {
        MateRequests.Validate(request.Type, request.DistanceMm, request.AngleDeg, request.Alignment, request.Flip);
        if (request.Entities.Count != 2) throw MateRequests.Invalid("/entities", $"a mate joins two sides; {request.Entities.Count} were given");
        var a = _a = MateEntities.Resolve(Services.References, doc, asm, request.Type, request.Entities[0], 0);
        var b = _b = MateEntities.Resolve(Services.References, doc, asm, request.Type, request.Entities[1], 1);
        if (a.Info.Component == null && b.Info.Component == null)
            throw MateRequests.Invalid("/entities/1", "both sides are the assembly's own; a mate joins a component to another component or to the assembly");
        if (a.Info.Component != null && string.Equals(a.Info.Component, b.Info.Component, StringComparison.OrdinalIgnoreCase))
            throw MateRequests.Invalid("/entities/1", $"both sides belong to {a.Info.Component}; a mate joins two components (or a component and the assembly)");
        string? refusal = MateGeometry.Refusal(request.Type, MateGeometry.ShapeOf(a.Info), MateGeometry.ShapeOf(b.Info));
        if (refusal != null)
            throw new SwAgentException(ErrorCode.InvalidArgument, refusal, new JsonObject { ["field"] = "/entities", ["accepts"] = MateGeometry.Accepts(request.Type) });
        if (request.Name != null && asm.MateByName(request.Name) != null)
            throw MateRequests.Invalid("/name", $"{doc.Info().Title} already has a mate named {request.Name}");
        if (request.Type == MateType.Lock)
        {
            _lockA = asm.Read(a.Entity).Transform;
            _lockB = asm.Read(b.Entity).Transform;
        }
        var spec = new MateSpec
        {
            Type = request.Type, Entities = new[] { a.Entity, b.Entity }, Alignment = request.Alignment, Flip = request.Flip == true,
            DistanceMm = request.DistanceMm, AngleDeg = request.AngleDeg, Name = request.Name,
        };
        var mate = asm.AddMate(spec, out string? error);
        if (mate != null)
        {
            string made = asm.ReadMate(mate).Name;
            tx.Made(new CreatedItem { Name = made, Kind = ReferenceKind.Mate, Reference = Services.References.Issue(doc, mate, doc.Describe(mate)) },
                () => asm.DeleteMates(new[] { mate }));
        }
        if (mate == null || error != null)
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not make the {WireNames.ForEnum(request.Type)} mate: {error ?? "it returned no mate"}",
                new JsonObject { ["mate_error"] = error });
        }
        asm.Rebuild();
        var info = MateChecks.Describe(Services.References, doc, asm, mate);
        context.Log.Info($"mate.add: {info.Name} ({WireNames.ForEnum(request.Type)}) between {a.Info.Component ?? "the assembly"} and {b.Info.Component ?? "the assembly"}");
        return new MateAddResult { Mate = info };
    }

    /// <summary>The mate is there without an error, and the condition it imposes holds, measured between its sides as they are now.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, MateAddRequest request, MateAddResult result)
    {
        var mate = asm.MateByName(result.Mate.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported {result.Mate.Name} made, but the assembly has no such mate", new JsonObject { ["check"] = "exists" });
        var facts = asm.ReadMate(mate);
        if (facts.Error != null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{facts.Name} has the mate error {facts.Error}", new JsonObject { ["check"] = "mate_error", ["measured"] = facts.Error });
        result.Measured = request.Type == MateType.Lock
            ? MateGeometry.Locked(_lockA!, _lockB!, asm.Read(_a!.Entity).Transform, asm.Read(_b!.Entity).Transform).ToList()
            : MateChecks.Measure(doc, request.Type,
                MateChecks.Again(Services.References, doc, _a!, "/entities/0/entity"),
                MateChecks.Again(Services.References, doc, _b!, "/entities/1/entity"),
                request.DistanceMm, request.AngleDeg);
        MateChecks.Require(result.Measured, facts.Name);
        result.Mate.Alignment = facts.Alignment;
    }
}
