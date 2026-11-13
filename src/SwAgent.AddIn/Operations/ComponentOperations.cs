using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>assembly.components (design 455, Components): read-only.</summary>
public sealed class AssemblyComponentsOperation : Operation<AssemblyComponentsRequest, AssemblyComponentsResult>
{
    readonly AddInServices _services;

    public AssemblyComponentsOperation(AddInServices services)
    {
        _services = services;
    }

    protected override AssemblyComponentsResult Run(AssemblyComponentsRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        var result = new AssemblyComponentsResult { Document = doc.Info() };
        foreach (var c in asm.Components(request.Recursive != true))
        {
            var info = asm.Read(c);
            info.Reference = _services.References.Issue(doc, c, doc.Describe(c));
            result.Components.Add(info);
        }
        return result;
    }

    /// <summary>Read again, the assembly has the same components: nothing changed them while they were read.</summary>
    protected override void Verify(AssemblyComponentsRequest request, AssemblyComponentsResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        var now = asm.Components(request.Recursive != true).Select(c => asm.Read(c).Name).ToList();
        if (!now.SequenceEqual(result.Components.Select(c => c.Name)))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the components changed while they were read ({result.Components.Count}, now {now.Count}); ask again");
        }
    }
}

/// <summary>component.place (design 455, Components).</summary>
public sealed class ComponentPlaceOperation : AssemblyChangeOperation<ComponentPlaceRequest, ComponentPlaceResult>
{
    /// <summary>Whether the component had mates, so a solve may have moved it (one request runs at a time).</summary>
    bool _mated;

    public ComponentPlaceOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ComponentPlaceRequest request) => request.Document;

    protected override ComponentPlaceResult Change(ISwDocument doc, ISwAssembly asm, ComponentPlaceRequest request, ModelTransaction tx, IOperationContext context)
    {
        var component = Component(doc, asm, request.Component, "component");
        var info = asm.Read(component);
        Placeable(doc, asm, component, info);
        var target = request.XAxis == null && request.ZAxis == null
            ? new Transform { Origin = request.Origin, XAxis = info.Transform.XAxis, YAxis = info.Transform.YAxis, ZAxis = info.Transform.ZAxis }
            : Placement.Make(request.Origin, request.XAxis, request.ZAxis, "x_axis", "z_axis");
        var old = info.Transform;
        _mated = info.Mates > 0;
        tx.Edited(new ChangedItem { Name = info.Name, Property = "position", Before = $"{old.Origin} mm", After = $"{target.Origin} mm" },
            () => asm.SetTransform(component, old));
        asm.SetTransform(component, target);
        if (_mated) asm.Rebuild();
        var now = Describe(doc, asm, component);
        bool solved = _mated && Placement.Moved(target, now.Transform);
        context.Log.Info($"component.place: {info.Name} to {target.Origin}{(solved ? $", solved to {now.Transform.Origin}" : "")}");
        return new ComponentPlaceResult { Component = now, Solved = solved };
    }

    /// <summary>A fixed component, one its mates hold completely, and one carried by a rigid subassembly cannot be placed.</summary>
    static void Placeable(ISwDocument doc, ISwAssembly asm, object component, ComponentInfo info)
    {
        if (info.Fixed)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{info.Name} is fixed; float it first (component.set with fixed: false)",
                new JsonObject { ["field"] = "component", ["component"] = info.Name });
        }
        if (info.Constraint == Constraint.FullyDefined)
        {
            var mates = asm.MatesOf(component);
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Name} is fully defined by its mates ({string.Join(", ", mates)}), so it cannot move; change or delete a mate first",
                new JsonObject { ["field"] = "component", ["component"] = info.Name, ["mates"] = mates.Select(m => (object?)m).ToList() });
        }
        if (info.Parent != null && asm.ComponentByName(info.Parent) is object parent && asm.Read(parent).SolveAs != SolveAs.Flexible)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{info.Name} is inside the rigid subassembly {info.Parent}, which carries it; place {info.Parent}, or make it flexible (component.set with solve_as: flexible)",
                new JsonObject { ["field"] = "component", ["component"] = info.Name, ["subassembly"] = info.Parent });
        }
    }

    /// <summary>An unmated component reads back where it was put; a mated one may have been solved elsewhere, which the result says.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, ComponentPlaceRequest request, ComponentPlaceResult result)
    {
        if (_mated) return;
        var now = asm.Read(asm.ComponentByName(result.Component.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Component.Name} is no longer in the assembly"));
        if (Placement.OriginMoved(new Transform { Origin = request.Origin }, now.Transform))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{now.Name} is at {now.Transform.Origin}, not {request.Origin}",
                new JsonObject { ["check"] = "origin", ["expected"] = request.Origin.ToString(), ["measured"] = now.Transform.Origin.ToString() });
        }
    }
}

/// <summary>component.set (design 455, Components): one operation for a component's state, every edit recorded and put back on failure.</summary>
public sealed class ComponentSetOperation : AssemblyChangeOperation<ComponentSetRequest, ComponentSetResult>
{
    public ComponentSetOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ComponentSetRequest request) => request.Document;

    protected override ComponentSetResult Change(ISwDocument doc, ISwAssembly asm, ComponentSetRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Fixed == null && request.Suppression == null && request.Configuration == null && request.SolveAs == null && request.Hidden == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, "give at least one of fixed, suppression, configuration, solve_as and hidden",
                new JsonObject { ["field"] = "components" });
        }
        if (request.Components.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "components is empty; name at least one", new JsonObject { ["field"] = "components" });
        var targets = new System.Collections.Generic.List<(object Component, ComponentInfo Info)>();
        for (int i = 0; i < request.Components.Count; i++)
        {
            var component = Component(doc, asm, request.Components[i], $"/components/{i}");
            var info = asm.Read(component);
            if (request.SolveAs != null && !info.Subassembly)
            {
                throw new SwAgentException(ErrorCode.InvalidArgument, $"{info.Name} is a part; solve_as is for subassemblies",
                    new JsonObject { ["field"] = $"/components/{i}", ["component"] = info.Name });
            }
            if (!targets.Exists(t => t.Info.Name == info.Name)) targets.Add((component, info));
        }
        foreach (var (component, info) in targets)
        {
            // Resolve first, so the other properties act on a loaded component; suppress last, after them.
            if (request.Suppression == Suppression.Resolved) SetSuppression(asm, tx, component, info, Suppression.Resolved);
            if (request.Configuration != null && info.Configuration != request.Configuration)
            {
                string old = info.Configuration ?? "";
                tx.Edited(Item(info, "configuration", old, request.Configuration), () => asm.SetConfiguration(component, old));
                asm.SetConfiguration(component, request.Configuration);
            }
            if (request.SolveAs is SolveAs solve && info.SolveAs != solve)
            {
                var old = info.SolveAs ?? SolveAs.Rigid;
                tx.Edited(Item(info, "solve_as", WireNames.ForEnum(old), WireNames.ForEnum(solve)), () => asm.SetSolveAs(component, old));
                asm.SetSolveAs(component, solve);
            }
            if (request.Fixed is bool fix && info.Fixed != fix)
            {
                bool old = info.Fixed;
                tx.Edited(Item(info, "fixed", Bool(old), Bool(fix)), () => asm.SetFixed(component, old));
                asm.SetFixed(component, fix);
            }
            if (request.Hidden is bool hide && info.Hidden != hide)
            {
                bool old = info.Hidden;
                tx.Edited(Item(info, "hidden", Bool(old), Bool(hide)), () => asm.SetHidden(component, old));
                asm.SetHidden(component, hide);
            }
            if (request.Suppression == Suppression.Suppressed) SetSuppression(asm, tx, component, info, Suppression.Suppressed);
        }
        asm.Rebuild();
        var result = new ComponentSetResult();
        foreach (var (component, _) in targets) result.Components.Add(Describe(doc, asm, component));
        context.Log.Info($"component.set: {string.Join(", ", targets.Select(t => t.Info.Name))}: {tx.Changed.Count} change(s)");
        return result;
    }

    static void SetSuppression(ISwAssembly asm, ModelTransaction tx, object component, ComponentInfo info, Suppression to)
    {
        var from = info.State == ComponentState.Suppressed || info.State == ComponentState.Missing ? Suppression.Suppressed : Suppression.Resolved;
        if (from == to && info.State != ComponentState.Lightweight) return;
        if (!asm.SetSuppression(component, to))
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                $"SOLIDWORKS did not make {info.Name} {WireNames.ForEnum(to)}{(info.State == ComponentState.Missing ? ": its file is missing" : "")}",
                new JsonObject { ["component"] = info.Name });
        }
        tx.Edited(Item(info, "suppression", WireNames.ForEnum(info.State), WireNames.ForEnum(to)), () => asm.SetSuppression(component, from));
    }

    static ChangedItem Item(ComponentInfo info, string property, string before, string after) =>
        new ChangedItem { Name = info.Name, Property = property, Before = before, After = after };

    static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Every component reads back every property as asked.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, ComponentSetRequest request, ComponentSetResult result)
    {
        foreach (var asked in result.Components)
        {
            var now = asm.Read(asm.ComponentByName(asked.Name)
                ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{asked.Name} is no longer in the assembly"));
            if (request.Fixed is bool fix && now.Fixed != fix) throw Failed(now, "fixed", Bool(fix), Bool(now.Fixed));
            if (request.Suppression is Suppression s && (s == Suppression.Suppressed) != (now.State == ComponentState.Suppressed))
                throw Failed(now, "suppression", WireNames.ForEnum(s), WireNames.ForEnum(now.State));
            if (request.Configuration != null && now.Configuration != request.Configuration)
                throw Failed(now, "configuration", request.Configuration, now.Configuration ?? "");
            if (request.SolveAs is SolveAs solve && now.SolveAs != solve)
                throw Failed(now, "solve_as", WireNames.ForEnum(solve), now.SolveAs == null ? "" : WireNames.ForEnum(now.SolveAs.Value));
            if (request.Hidden is bool hide && now.Hidden != hide) throw Failed(now, "hidden", Bool(hide), Bool(now.Hidden));
        }
    }

    static SwAgentException Failed(ComponentInfo now, string property, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"{now.Name}'s {property} is {measured}, not {expected}{(property == "configuration" ? ": its file may have no configuration of that name" : "")}",
            new JsonObject { ["check"] = property, ["component"] = now.Name, ["expected"] = expected, ["measured"] = measured });
}

/// <summary>
/// component.delete (design 455, Components). Nothing is recorded to roll back: a deleted component and its mates cannot
/// be made again as they were, so a failed check reports what is left instead.
/// </summary>
public sealed class ComponentDeleteOperation : AssemblyChangeOperation<ComponentDeleteRequest, ComponentDeleteResult>
{
    /// <summary>The assembly's mate count before the delete, and the instances that go with the patterns (one request runs at a time).</summary>
    int _matesBefore;
    System.Collections.Generic.List<string> _instances = new System.Collections.Generic.List<string>();

    public ComponentDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ComponentDeleteRequest request) => request.Document;

    protected override ComponentDeleteResult Change(ISwDocument doc, ISwAssembly asm, ComponentDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Components.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "components is empty; name at least one", new JsonObject { ["field"] = "components" });
        var patterns = new System.Collections.Generic.List<(object Pattern, ComponentPatternFacts Facts)>();
        var doomed = new System.Collections.Generic.List<(object Component, ComponentInfo Info, int Index)>();
        for (int i = 0; i < request.Components.Count; i++)
        {
            string field = $"/components/{i}";
            var pattern = PatternLookup.TryFind(Services.References, doc, asm, request.Components[i], field);
            if (pattern != null)
            {
                var facts = asm.ReadPattern(pattern);
                if (!patterns.Exists(p => p.Facts.Name == facts.Name)) patterns.Add((pattern, facts));
                continue;
            }
            var component = Component(doc, asm, request.Components[i], field);
            var info = asm.Read(component);
            if (info.Parent != null)
            {
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"{info.Name} is inside the subassembly {info.Parent}; open {info.Parent}'s own document to delete it there, or delete {info.Parent}",
                    new JsonObject { ["field"] = field, ["component"] = info.Name, ["subassembly"] = info.Parent });
            }
            if (!doomed.Exists(d => d.Info.Name == info.Name)) doomed.Add((component, info, i));
        }
        var membership = PatternLookup.Membership(asm);
        var going = new System.Collections.Generic.HashSet<string>(patterns.Select(p => p.Facts.Name), System.StringComparer.OrdinalIgnoreCase);
        foreach (var (_, info, index) in doomed)
        {
            string field = $"/components/{index}";
            if (membership.InstanceOf.TryGetValue(info.Name, out var owner))
            {
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"{info.Name} is an instance of the pattern {owner}; delete the pattern (component.delete with {owner}), which deletes all its instances, rather than one of them",
                    new JsonObject { ["field"] = field, ["component"] = info.Name, ["pattern"] = owner });
            }
            if (membership.SeedOf.TryGetValue(info.Name, out var of) && of.Any(p => !going.Contains(p)))
            {
                var kept = of.Where(p => !going.Contains(p)).ToList();
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"{info.Name} is the seed of {string.Join(", ", kept)}; delete the pattern in the same request ([\"{kept[0]}\", \"{info.Name}\"]) or on its own first",
                    new JsonObject { ["field"] = field, ["component"] = info.Name, ["patterns"] = kept.Select(p => (object?)p).ToList() });
            }
        }
        var instances = patterns.SelectMany(p => p.Facts.Instances).ToList();
        _instances = instances.Select(c => asm.Identify(c).Name).ToList();
        var mates = doomed.SelectMany(d => asm.MatesOf(d.Component)).Concat(instances.SelectMany(i => asm.MatesOf(i))).Distinct().ToList();
        _matesBefore = asm.MateCount();
        if (patterns.Count > 0) asm.DeletePatterns(patterns.Select(p => p.Pattern).ToList());
        if (doomed.Count > 0) asm.Delete(doomed.Select(d => d.Component).ToList());
        context.Log.Info($"component.delete: {string.Join(", ", patterns.Select(p => p.Facts.Name).Concat(doomed.Select(d => d.Info.Name)))}{(mates.Count > 0 ? $" with mates {string.Join(", ", mates)}" : "")}");
        return new ComponentDeleteResult
        {
            Deleted = doomed.Select(d => d.Info.Name).ToList(),
            MatesDeleted = mates,
            PatternsDeleted = patterns.Select(p => p.Facts.Name).ToList(),
            InstancesDeleted = _instances.ToList(),
        };
    }

    /// <summary>None of the components, patterns or instances is left, and the assembly has as many fewer mates as went with them.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, ComponentDeleteRequest request, ComponentDeleteResult result)
    {
        var left = result.Deleted.Concat(_instances).Where(n => asm.ComponentByName(n) != null)
            .Concat(result.PatternsDeleted.Where(p => asm.PatternByName(p) != null)).ToList();
        if (left.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS did not delete {string.Join(", ", left)}",
                new JsonObject { ["check"] = "deleted", ["left"] = left.Select(l => (object?)l).ToList() });
        }
        int mates = asm.MateCount();
        if (mates != _matesBefore - result.MatesDeleted.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the assembly has {mates} mates, not {_matesBefore - result.MatesDeleted.Count}: the mates of the deleted components were not all deleted with them",
                new JsonObject { ["check"] = "mates", ["expected"] = _matesBefore - result.MatesDeleted.Count, ["measured"] = mates });
        }
    }
}

/// <summary>assembly.insert (design 455, Components).</summary>
public sealed class AssemblyInsertOperation : AssemblyChangeOperation<AssemblyInsertRequest, AssemblyInsertResult>
{
    /// <summary>Whether the request's component is to be fixed, decided before the insert (one request runs at a time).</summary>
    bool _fix;

    public AssemblyInsertOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(AssemblyInsertRequest request) => request.Document;

    protected override AssemblyInsertResult Change(ISwDocument doc, ISwAssembly asm, AssemblyInsertRequest request, ModelTransaction tx, IOperationContext context)
    {
        string path = PathRules.Existing(request.Path, "path", ".SLDPRT", ".SLDASM");
        if (doc.Info().Path is string own && DocumentKeys.Same(Path.GetFullPath(own), path))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{path} is the assembly itself; an assembly cannot contain itself",
                new JsonObject { ["field"] = "path", ["path"] = path });
        }
        var at = Placement.Make(request.At, request.XAxis, request.ZAxis, "x_axis", "z_axis");
        bool fix = _fix = request.Fixed ?? asm.Components(true).Count == 0;
        var component = asm.Insert(path, request.Configuration, at)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not insert {path} into {doc.Info().Title}",
                new JsonObject { ["path"] = path });
        var made = asm.Read(component);
        tx.Made(new CreatedItem { Name = made.Name, Kind = ReferenceKind.Component, Reference = Services.References.Issue(doc, component, doc.Describe(component)) },
            () => asm.Delete(new[] { component }));
        if (made.Fixed != fix) asm.SetFixed(component, fix);
        context.Log.Info($"assembly.insert: {made.Name} from {path} at {at.Origin}{(fix ? ", fixed" : "")}");
        return new AssemblyInsertResult { Component = Describe(doc, asm, component) };
    }

    /// <summary>The component is there again by its name, of the file and configuration asked, fixed or floating as asked, and where it was put.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, AssemblyInsertRequest request, AssemblyInsertResult result)
    {
        var component = asm.ComponentByName(result.Component.Name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported {result.Component.Name} inserted, but the assembly has no such component");
        var now = asm.Read(component);
        string path = Path.GetFullPath(request.Path);
        if (now.Path == null || !DocumentKeys.Same(Path.GetFullPath(now.Path), path))
            throw Failed($"{now.Name} is {now.Path ?? "no file"}, not {path}", "path", path, now.Path);
        if (request.Configuration != null && now.Configuration != request.Configuration)
            throw Failed($"{now.Name} uses the configuration {now.Configuration}, not {request.Configuration}: the file has no configuration of that name", "configuration", request.Configuration, now.Configuration);
        if (now.Fixed != _fix) throw Failed($"{now.Name} is {(now.Fixed ? "fixed" : "floating")}, not {(_fix ? "fixed" : "floating")}", "fixed", _fix, now.Fixed);
        var at = Placement.Make(request.At, request.XAxis, request.ZAxis, "x_axis", "z_axis");
        if (Placement.Moved(at, now.Transform))
            throw Failed($"{now.Name} landed at {now.Transform}, not {at}", "transform", at.ToString(), now.Transform.ToString());
    }

    static SwAgentException Failed(string message, string check, object? expected, object? measured) =>
        new SwAgentException(ErrorCode.VerificationFailed, message,
            new JsonObject { ["check"] = check, ["expected"] = expected, ["measured"] = measured });
}
