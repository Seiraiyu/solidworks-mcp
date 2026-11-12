using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// A component pattern (design 455, Component patterns), made as one assembly change: the seeds checked, where every
/// instance must land worked out before SOLIDWORKS is asked, the pattern made and recorded so a rollback deletes it,
/// then every instance read back and measured. The subclasses say what to make and how to measure it.
/// </summary>
public abstract class ComponentPatternOperation<TRequest> : AssemblyChangeOperation<TRequest, ComponentPatternResult>
    where TRequest : class
{
    protected ComponentPatternOperation(AddInServices services)
        : base(services)
    {
    }

    /// <summary>The seeds' names and placements before the pattern was made (one request runs at a time).</summary>
    protected List<(string Name, Transform At)> SeedPlaces { get; private set; } = new List<(string Name, Transform At)>();

    protected abstract ComponentPatternKind Kind { get; }

    protected abstract IReadOnlyList<string> SeedsOf(TRequest request);

    /// <summary>Checks the rest of the request, works out where the instances must land, and asks SOLIDWORKS; null when it made nothing.</summary>
    protected abstract object? Make(ISwDocument doc, ISwAssembly asm, TRequest request, IReadOnlyList<object> seeds, out string tried);

    /// <summary>Each instance paired with where it must be; throws verification_failed when one is not there.</summary>
    protected abstract List<PatternInstance> Measure(TRequest request, IReadOnlyList<(string Name, Transform At)> instances, ComponentPatternResult result);

    /// <summary>The seeds and the instances: the pattern set how far each is defined.</summary>
    protected override IEnumerable<string> Joined(ComponentPatternResult result) => result.Pattern.Seeds.Concat(result.Pattern.Instances);

    protected sealed override ComponentPatternResult Change(ISwDocument doc, ISwAssembly asm, TRequest request, ModelTransaction tx, IOperationContext context)
    {
        var seeds = PatternSeeds.Resolve(Services.References, doc, asm, SeedsOf(request));
        SeedPlaces = seeds.Select(s => (s.Info.Name, s.Info.Transform)).ToList();
        string kind = WireNames.ForEnum(Kind), names = string.Join(", ", SeedPlaces.Select(s => s.Name));
        var pattern = Make(doc, asm, request, seeds.Select(s => s.Component).ToList(), out string tried);
        if (pattern == null)
        {
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no {kind} component pattern of {names}. Tried: {tried}",
                new JsonObject { ["tried"] = tried });
        }
        var facts = asm.ReadPattern(pattern);
        tx.Made(new CreatedItem { Name = facts.Name, Kind = ReferenceKind.Feature, Reference = Services.References.Issue(doc, pattern, doc.Describe(pattern)) },
            () => asm.DeletePatterns(new[] { pattern }));
        asm.Rebuild();
        context.Log.Info($"{Name}: {facts.Name} of {names}, {facts.Instances.Count} instances; {tried}");
        return new ComponentPatternResult { Pattern = Info(doc, asm, pattern) };
    }

    /// <summary>The pattern is there, of its kind, without an error, and every instance is where it must be.</summary>
    protected sealed override void Check(ISwDocument doc, ISwAssembly asm, TRequest request, ComponentPatternResult result)
    {
        var pattern = asm.PatternByName(result.Pattern.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported {result.Pattern.Name} made, but the assembly has no such component pattern",
                new JsonObject { ["check"] = "exists" });
        var facts = asm.ReadPattern(pattern);
        if (facts.Kind != Kind)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{facts.Name} is a {WireNames.ForEnum(facts.Kind)} pattern, not {WireNames.ForEnum(Kind)}",
                new JsonObject { ["check"] = "kind", ["expected"] = WireNames.ForEnum(Kind), ["measured"] = WireNames.ForEnum(facts.Kind) });
        }
        if (facts.Error != null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{facts.Name} has the rebuild error {facts.Error}", new JsonObject { ["check"] = "pattern_error", ["measured"] = facts.Error });
        var instances = facts.Instances.Select(i =>
        {
            var info = asm.Read(i);
            return (info.Name, info.Transform);
        }).ToList();
        result.Instances = Measure(request, instances, result);
        result.Pattern = Info(doc, asm, pattern);
    }

    ComponentPatternInfo Info(ISwDocument doc, ISwAssembly asm, object pattern)
    {
        var facts = asm.ReadPattern(pattern);
        var info = new ComponentPatternInfo { Name = facts.Name, Kind = facts.Kind, Error = facts.Error, Reference = Services.References.Issue(doc, pattern, doc.Describe(pattern)) };
        info.Seeds.AddRange(facts.Seeds.Select(s => asm.Identify(s).Name));
        info.Instances.AddRange(facts.Instances.Select(i => asm.Identify(i).Name));
        return info;
    }
}

/// <summary>component.linear_pattern (design 455, Component patterns).</summary>
public sealed class ComponentLinearPatternOperation : ComponentPatternOperation<ComponentLinearPatternRequest>
{
    IReadOnlyList<PatternReading> _readings = Array.Empty<PatternReading>();

    public ComponentLinearPatternOperation(AddInServices services)
        : base(services)
    {
    }

    protected override ComponentPatternKind Kind => ComponentPatternKind.Linear;

    protected override string? DocumentOf(ComponentLinearPatternRequest request) => request.Document;

    protected override IReadOnlyList<string> SeedsOf(ComponentLinearPatternRequest request) => request.Components;

    protected override object? Make(ISwDocument doc, ISwAssembly asm, ComponentLinearPatternRequest r, IReadOnlyList<object> seeds, out string tried)
    {
        int count = PatternRequests.Count(r.Count, "/count");
        double spacing = PatternRequests.Spacing(r.SpacingMm, "/spacing_mm");
        var d1 = PatternEntities.Resolve(Services.References, doc, r.Direction, r.DirectionComponent, "/direction", "/direction_component");
        var v1 = PatternGeometry.DirectionOf(d1.Info) ?? throw PatternRequests.NotADirection("/direction", d1.Info);
        var spec = new ComponentLinearSpec { Seeds = seeds, Direction = d1.Entity, SpacingMm = spacing, Count = count, Reverse = r.Reverse == true };
        Vector3? v2 = null;
        if (r.Direction2 is ComponentPatternDirection second)
        {
            spec.Count2 = PatternRequests.Count(second.Count, "/direction2/count");
            spec.SpacingMm2 = PatternRequests.Spacing(second.SpacingMm, "/direction2/spacing_mm");
            var d2 = PatternEntities.Resolve(Services.References, doc, second.Direction, second.DirectionComponent, "/direction2/direction", "/direction2/direction_component");
            v2 = PatternGeometry.DirectionOf(d2.Info) ?? throw PatternRequests.NotADirection("/direction2/direction", d2.Info);
            if (Math.Abs(Math.Abs(PatternGeometry.Dot(PatternGeometry.Unit(v1), PatternGeometry.Unit(v2))) - 1) < 1e-9)
                throw PatternRequests.Invalid("/direction2/direction", "direction2 is parallel to direction; a grid needs two directions that cross");
            spec.Direction2 = d2.Entity;
            spec.Reverse2 = second.Reverse == true;
        }
        _readings = PatternGeometry.Linear(SeedPlaces, v1, spacing, count, v2, spec.SpacingMm2, spec.Count2);
        return asm.LinearPattern(spec, out tried);
    }

    protected override List<PatternInstance> Measure(ComponentLinearPatternRequest request, IReadOnlyList<(string Name, Transform At)> instances, ComponentPatternResult result)
    {
        var (reading, list) = PatternGeometry.Match(_readings, instances, checkAxes: true);
        result.Sense = reading.Sense;
        return list;
    }
}
