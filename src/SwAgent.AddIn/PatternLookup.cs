using System;
using System.Collections.Generic;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>What the component pattern operations check of a request before anything is asked of SOLIDWORKS.</summary>
public static class PatternRequests
{
    public static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });

    public static int Count(int? count, string field)
    {
        if (count == null) throw Invalid(field, $"{field} is needed: how many instances, the seed included (2 to 500)");
        if (count < 2 || count > 500) throw Invalid(field, $"{field} must be 2 to 500, not {count}");
        return count.Value;
    }

    public static double Spacing(double? mm, string field)
    {
        if (mm == null) throw Invalid(field, $"{field} is needed: the distance between instances, mm");
        if (!(mm > 0 && mm <= 10_000)) throw Invalid(field, $"{field} must be more than 0 and at most 10000 mm, not {mm}");
        return mm.Value;
    }

    public static double Angle(double? deg, string field, double fallback)
    {
        double a = deg ?? fallback;
        if (!(a > 0 && a <= 360)) throw Invalid(field, $"{field} must be more than 0 and at most 360 degrees, not {a}");
        return a;
    }

    public static SwAgentException NotADirection(string field, EntityInfo e) =>
        Invalid(field, $"{field} names a {WireNames.ForEnum(e.Geometry)} {WireNames.ForEnum(e.Kind)}; a linear pattern runs along a straight edge, an axis, or a plane's or planar face's normal");

    public static SwAgentException NotAnAxis(string field, EntityInfo e) =>
        Invalid(field, $"{field} names a {WireNames.ForEnum(e.Geometry)} {WireNames.ForEnum(e.Kind)}; a circular pattern turns about an axis, a straight or circular edge, or a cylindrical or conical face");
}

/// <summary>Which components are seeds and which are instances of the assembly's component patterns, by name.</summary>
public sealed class PatternMembership
{
    public Dictionary<string, string> InstanceOf { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> SeedOf { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Finds the component pattern a request names, and what the assembly's patterns are made of.</summary>
public static class PatternLookup
{
    /// <summary>
    /// The pattern <paramref name="text"/> names: its feature reference (r1.feature.…) or its name (LocalLPattern1).
    /// Null when the text names no pattern (a component, by reference or name); invalid_argument for a feature reference
    /// that is not a component pattern.
    /// </summary>
    public static object? TryFind(ReferenceService references, ISwDocument doc, ISwAssembly asm, string text, string field)
    {
        if (References.IsReference(text))
        {
            if (References.KindOf(text) != ReferenceKind.Feature) return null;
            var feature = references.Resolve(doc, text, field, ReferenceKind.Feature).Entity;
            string name = doc.Describe(feature).Name ?? "";
            return asm.PatternByName(name)
                ?? throw PatternRequests.Invalid(field, $"{field} names the feature {name}, which is not a component pattern; component.delete deletes components and component patterns");
        }
        return asm.PatternByName(text);
    }

    public static PatternMembership Membership(ISwAssembly asm)
    {
        var m = new PatternMembership();
        foreach (var p in asm.Patterns())
        {
            var facts = asm.ReadPattern(p);
            foreach (var i in facts.Instances) m.InstanceOf[asm.Identify(i).Name] = facts.Name;
            foreach (var s in facts.Seeds)
            {
                string name = asm.Identify(s).Name;
                if (!m.SeedOf.TryGetValue(name, out var list)) m.SeedOf[name] = list = new List<string>();
                list.Add(facts.Name);
            }
        }
        return m;
    }
}

/// <summary>
/// The seeds of a component pattern (design 455, Component patterns): top-level, resolved components that are not
/// themselves pattern instances, each once, in the order asked. Field pointers are /components/&lt;i&gt;.
/// </summary>
public static class PatternSeeds
{
    public static List<(object Component, ComponentInfo Info)> Resolve(ReferenceService references, ISwDocument doc, ISwAssembly asm, IReadOnlyList<string> components)
    {
        if (components.Count == 0) throw PatternRequests.Invalid("/components", "components is empty; name at least one component to repeat");
        var membership = PatternLookup.Membership(asm);
        var seeds = new List<(object Component, ComponentInfo Info)>();
        for (int i = 0; i < components.Count; i++)
        {
            string field = $"/components/{i}";
            var component = ComponentLookup.Find(references, doc, asm, components[i], field);
            var info = asm.Read(component);
            if (info.Parent != null)
                throw PatternRequests.Invalid(field, $"{info.Name} is inside the subassembly {info.Parent}; pattern {info.Parent} itself, or pattern the component in {info.Parent}'s own document");
            if (info.State != ComponentState.Resolved)
            {
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"{info.Name} is {WireNames.ForEnum(info.State)}, so it cannot be repeated; {ReferenceService.Remedy(new ComponentIdentity(info.Name, info.Path, info.State))}",
                    new JsonObject { ["field"] = field, ["component"] = info.Name, ["state"] = WireNames.ForEnum(info.State) });
            }
            if (membership.InstanceOf.TryGetValue(info.Name, out var pattern))
                throw PatternRequests.Invalid(field, $"{info.Name} is an instance of the pattern {pattern}; repeat that pattern's seed instead");
            if (!seeds.Exists(s => string.Equals(s.Info.Name, info.Name, StringComparison.OrdinalIgnoreCase))) seeds.Add((component, info));
        }
        return seeds;
    }
}

/// <summary>
/// A pattern's direction or axis (design 455, Assembly references): a reference issued in the assembly, name@component or
/// the assembly's own plane, or with a component field a reference in that component's own part (GetCorresponding).
/// </summary>
public static class PatternEntities
{
    static readonly ReferenceKind[] Accepted = { ReferenceKind.Face, ReferenceKind.Edge, ReferenceKind.Plane, ReferenceKind.Axis };

    public static ResolvedSide Resolve(ReferenceService references, ISwDocument doc, string text, string? component, string field, string componentField)
    {
        if (string.IsNullOrWhiteSpace(text)) throw PatternRequests.Invalid(field, $"{field} is empty; name an edge, axis, plane or face");
        var entity = component == null
            ? references.Resolve(doc, text, field, Accepted).Entity
            : references.ResolveInComponent(doc, text, component, field, componentField, Accepted).Entity;
        return new ResolvedSide(entity, references.Describe(doc, entity));
    }
}
