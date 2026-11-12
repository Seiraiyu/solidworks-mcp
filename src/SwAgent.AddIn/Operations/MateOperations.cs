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

    /// <summary>The two components the mate joins: its constrained state changed even when nothing moved.</summary>
    protected override IEnumerable<string> Joined(MateAddResult result) =>
        result.Mate.Entities.Where(e => e.Component != null).Select(e => e.Component!);

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

/// <summary>mate.edit (design 455, Mates): a standard mate's value, alignment, flip or suppression, in place; old values put back on failure.</summary>
public sealed class MateEditOperation : AssemblyChangeOperation<MateEditRequest, MateEditResult>
{
    public MateEditOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(MateEditRequest request) => request.Document;

    /// <summary>The two components the mate joins: its constrained state changed even when nothing moved.</summary>
    protected override IEnumerable<string> Joined(MateEditResult result) =>
        result.Mate.Entities.Where(e => e.Component != null).Select(e => e.Component!);

    protected override MateEditResult Change(ISwDocument doc, ISwAssembly asm, MateEditRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.DistanceMm == null && request.AngleDeg == null && request.Alignment == null && request.Flip == null && request.Suppressed == null)
            throw MateRequests.Invalid("/mate", "give at least one of distance_mm, angle_deg, alignment, flip and suppressed");
        var mate = MateLookup.Find(Services.References, doc, asm, request.Mate, "/mate");
        var facts = asm.ReadMate(mate);
        if (!(facts.Type is MateType type))
            throw MateRequests.Invalid("/mate", $"{facts.Name} is a {facts.SolidWorksType} mate; mate.edit changes the standard mates, and api.call reaches the others");
        if (request.DistanceMm != null && type != MateType.Distance) throw MateRequests.Invalid("/distance_mm", $"{facts.Name} is a {WireNames.ForEnum(type)} mate; distance_mm is for distance mates");
        if (request.AngleDeg != null && type != MateType.Angle) throw MateRequests.Invalid("/angle_deg", $"{facts.Name} is a {WireNames.ForEnum(type)} mate; angle_deg is for angle mates");
        MateRequests.Validate(type, request.DistanceMm ?? facts.DistanceMm, request.AngleDeg ?? facts.AngleDeg, request.Alignment, request.Flip);

        if (request.Suppressed is bool suppress && suppress != facts.Suppressed)
        {
            bool was = facts.Suppressed;
            if (!asm.SetMateSuppressed(mate, suppress))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not {(suppress ? "suppress" : "unsuppress")} {facts.Name}", new JsonObject { ["mate"] = facts.Name });
            tx.Edited(Item(facts.Name, "suppressed", Bool(was), Bool(suppress)), () => asm.SetMateSuppressed(mate, was));
        }
        var old = Spec(type, facts, null);
        var now = Spec(type, facts, request);
        var changes = new List<ChangedItem>();
        if (request.DistanceMm is double d && d != facts.DistanceMm) changes.Add(Item(facts.Name, "distance_mm", Num(facts.DistanceMm), Num(d)));
        if (request.AngleDeg is double g && g != facts.AngleDeg) changes.Add(Item(facts.Name, "angle_deg", Num(facts.AngleDeg), Num(g)));
        if (request.Alignment is MateAlignment al && al != facts.Alignment) changes.Add(Item(facts.Name, "alignment", facts.Alignment == null ? "" : WireNames.ForEnum(facts.Alignment.Value), WireNames.ForEnum(al)));
        if (request.Flip is bool f && f != facts.Flipped) changes.Add(Item(facts.Name, "flip", Bool(facts.Flipped), Bool(f)));
        if (changes.Count > 0)
        {
            if (!asm.EditMate(mate, now))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not change {facts.Name}", new JsonObject { ["mate"] = facts.Name });
            foreach (var item in changes) tx.Edited(item, () => asm.EditMate(mate, old));
        }
        asm.Rebuild();
        context.Log.Info($"mate.edit: {facts.Name}: {tx.Changed.Count} change(s)");
        return new MateEditResult { Mate = MateChecks.Describe(Services.References, doc, asm, mate) };
    }

    static MateSpec Spec(MateType type, MateFacts f, MateEditRequest? r) => new MateSpec
    {
        Type = type,
        Alignment = r?.Alignment ?? f.Alignment,
        Flip = r?.Flip ?? f.Flipped,
        DistanceMm = r?.DistanceMm ?? f.DistanceMm,
        AngleDeg = r?.AngleDeg ?? f.AngleDeg,
    };

    static ChangedItem Item(string name, string property, string before, string after) =>
        new ChangedItem { Name = name, Property = property, Before = before, After = after };

    static string Bool(bool value) => value ? "true" : "false";

    static string Num(double? value) => value?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>Every property reads back as asked; an unsuppressed mate has no error and its condition holds, measured between its sides now.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, MateEditRequest request, MateEditResult result)
    {
        var mate = asm.MateByName(result.Mate.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Mate.Name} is no longer in the assembly", new JsonObject { ["check"] = "exists" });
        var facts = asm.ReadMate(mate);
        if (request.Suppressed is bool s && facts.Suppressed != s) throw Failed(facts.Name, "suppressed", Bool(s), Bool(facts.Suppressed));
        if (request.DistanceMm is double d && !(Math.Abs((facts.DistanceMm ?? double.NaN) - d) <= MateGeometry.DistanceToleranceMm))
            throw Failed(facts.Name, "distance_mm", Num(d), Num(facts.DistanceMm));
        if (request.AngleDeg is double g && !(Math.Abs((facts.AngleDeg ?? double.NaN) - g) <= MateGeometry.AngleToleranceDeg))
            throw Failed(facts.Name, "angle_deg", Num(g), Num(facts.AngleDeg));
        if (facts.Suppressed) return;
        if (facts.Error != null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{facts.Name} has the mate error {facts.Error}", new JsonObject { ["check"] = "mate_error", ["measured"] = facts.Error });
        if (facts.Type == MateType.Lock || facts.Entities.Count != 2) return;
        var a = facts.Entities[0].Entity;
        var b = facts.Entities[1].Entity;
        result.Measured = MateChecks.Measure(doc, facts.Type!.Value, (a, Services.References.Describe(doc, a)), (b, Services.References.Describe(doc, b)), facts.DistanceMm, facts.AngleDeg);
        MateChecks.Require(result.Measured, facts.Name);
    }

    static SwAgentException Failed(string mate, string property, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed, $"{mate}'s {property} is {measured}, not {expected}",
            new JsonObject { ["check"] = property, ["expected"] = expected, ["measured"] = measured });
}

/// <summary>
/// mate.delete (design 455, Mates). Nothing is recorded to roll back: a deleted mate cannot be made again exactly as it
/// was, so a failed check reports what is left. The components stay where the mates left them.
/// </summary>
public sealed class MateDeleteOperation : AssemblyChangeOperation<MateDeleteRequest, MateDeleteResult>
{
    int _matesBefore;

    public MateDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(MateDeleteRequest request) => request.Document;

    /// <summary>The components the mates joined, which stay where they are with fewer mates.</summary>
    protected override IEnumerable<string> Joined(MateDeleteResult result) => result.Stayed;

    protected override MateDeleteResult Change(ISwDocument doc, ISwAssembly asm, MateDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Mates.Count == 0) throw MateRequests.Invalid("/mates", "mates is empty; name at least one");
        var doomed = new List<(object Mate, MateFacts Facts)>();
        for (int i = 0; i < request.Mates.Count; i++)
        {
            var mate = MateLookup.Find(Services.References, doc, asm, request.Mates[i], $"/mates/{i}");
            var facts = asm.ReadMate(mate);
            if (!doomed.Exists(d => d.Facts.Name == facts.Name)) doomed.Add((mate, facts));
        }
        var stayed = doomed.SelectMany(d => d.Facts.Entities).Where(e => e.Component != null)
            .Select(e => asm.Identify(e.Component!).Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _matesBefore = asm.Mates().Count;
        asm.DeleteMates(doomed.Select(d => d.Mate).ToList());
        context.Log.Info($"mate.delete: {string.Join(", ", doomed.Select(d => d.Facts.Name))}");
        return new MateDeleteResult { Deleted = doomed.Select(d => d.Facts.Name).ToList(), Stayed = stayed };
    }

    /// <summary>None of the mates is left, and the assembly has that many fewer.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, MateDeleteRequest request, MateDeleteResult result)
    {
        var left = result.Deleted.Where(n => asm.MateByName(n) != null).ToList();
        if (left.Count > 0)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS did not delete {string.Join(", ", left)}",
                new JsonObject { ["check"] = "deleted", ["left"] = left.Select(l => (object?)l).ToList() });
        int now = asm.Mates().Count;
        if (now != _matesBefore - result.Deleted.Count)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the assembly has {now} mates, not {_matesBefore - result.Deleted.Count}",
                new JsonObject { ["check"] = "mates", ["expected"] = _matesBefore - result.Deleted.Count, ["measured"] = now });
    }
}

/// <summary>mate.list (design 455, Mates): read-only.</summary>
public sealed class MateListOperation : Operation<MateListRequest, MateListResult>
{
    readonly AddInServices _services;

    public MateListOperation(AddInServices services)
    {
        _services = services;
    }

    protected override MateListResult Run(MateListRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        string? only = request.Component == null ? null : asm.Identify(ComponentLookup.Find(_services.References, doc, asm, request.Component, "/component")).Name;
        var result = new MateListResult { Document = doc.Info() };
        foreach (var mate in asm.Mates())
        {
            var info = MateChecks.Describe(_services.References, doc, asm, mate);
            if (only == null || info.Entities.Any(e => string.Equals(e.Component, only, StringComparison.OrdinalIgnoreCase))) result.Mates.Add(info);
        }
        return result;
    }

    /// <summary>Read again, the assembly has the same mates: nothing changed them while they were read.</summary>
    protected override void Verify(MateListRequest request, MateListResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        var names = asm.Mates().Select(m => asm.ReadMate(m).Name).ToList();
        if (result.Mates.Any(m => !names.Contains(m.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the mates changed while they were read; ask again");
    }
}

/// <summary>assembly.dof (design 455, Degrees of freedom): read-only; never moves anything to find out.</summary>
public sealed class AssemblyDofOperation : Operation<AssemblyDofRequest, AssemblyDofResult>
{
    readonly AddInServices _services;

    public AssemblyDofOperation(AddInServices services)
    {
        _services = services;
    }

    protected override AssemblyDofResult Run(AssemblyDofRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        var targets = new List<object>();
        if (request.Components == null || request.Components.Count == 0) targets.AddRange(asm.Components(true));
        else
        {
            for (int i = 0; i < request.Components.Count; i++)
            {
                var c = ComponentLookup.Find(_services.References, doc, asm, request.Components[i], $"/components/{i}");
                if (!targets.Contains(c)) targets.Add(c);
            }
        }
        var result = new AssemblyDofResult { Document = doc.Info() };
        foreach (var c in targets)
        {
            var info = asm.Read(c);
            var dofs = info.Constraint == Constraint.UnderDefined && info.Mates > 0 ? asm.RemainingDofs(c) : null;
            var freedom = Freedom.Of(info.Name, info.Constraint, info.Mates, info.Transform, dofs);
            freedom.Reference = _services.References.Issue(doc, c, doc.Describe(c));
            result.Components.Add(freedom);
        }
        return result;
    }

    /// <summary>Read again, every component is as far defined as it was: nothing changed while it was read.</summary>
    protected override void Verify(AssemblyDofRequest request, AssemblyDofResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        foreach (var c in result.Components)
        {
            var now = asm.ComponentByName(c.Name) is object component ? asm.Read(component).Constraint : (Constraint?)null;
            if (now != c.Constraint)
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{c.Name} changed while it was read ({WireNames.ForEnum(c.Constraint)}, now {(now == null ? "gone" : WireNames.ForEnum(now.Value))}); ask again");
        }
    }
}
