using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the explode operations check of a request and read of an assembly, before anything is asked of SOLIDWORKS.</summary>
public static class ExplodeRequests
{
    /// <summary>x, y and z are the assembly's own right, top and front planes, whose normals are +X, +Y and +Z.</summary>
    public static string Direction(string text) => text.Trim().ToLowerInvariant() switch
    {
        "x" => "right",
        "y" => "top",
        "z" => "front",
        _ => text,
    };

    /// <summary>The active configuration, once the one the request names (if any) is it.</summary>
    public static string Configuration(ISwDocument doc, string? asked)
    {
        string active = doc.ActiveConfiguration();
        if (asked == null || string.Equals(asked, active, StringComparison.OrdinalIgnoreCase)) return active;
        if (!doc.ConfigurationNames().Contains(asked, StringComparer.OrdinalIgnoreCase))
            throw PatternRequests.Invalid("/configuration", $"{doc.Info().Title} has no configuration '{asked}'; its configurations are {string.Join(", ", doc.ConfigurationNames())}");
        throw PatternRequests.Invalid("/configuration", $"{asked} is not the active configuration ({active}); SOLIDWORKS makes and shows exploded views in the active one: activate {asked} with configuration.activate first");
    }

    /// <summary>The view the request names, as the active configuration spells it; invalid_argument naming the configuration it belongs to when it is another's.</summary>
    public static string View(ISwDocument doc, ISwAssembly asm, string view, string field = "/view")
    {
        if (string.IsNullOrWhiteSpace(view)) throw PatternRequests.Invalid(field, "view is empty; name an exploded view (explode.list lists them)");
        string active = doc.ActiveConfiguration();
        var here = asm.ExplodedViews(active);
        var found = here.FirstOrDefault(v => string.Equals(v, view, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        var elsewhere = doc.ConfigurationNames().Where(c => !string.Equals(c, active, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(c => asm.ExplodedViews(c).Contains(view, StringComparer.OrdinalIgnoreCase));
        if (elsewhere != null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{view} is an exploded view of {elsewhere}, not of the active configuration ({active}); activate {elsewhere} with configuration.activate first",
                new JsonObject { ["field"] = field, ["configuration"] = elsewhere });
        }
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{doc.Info().Title} has no exploded view '{view}' in {active}; its views there: {(here.Count == 0 ? "none" : string.Join(", ", here))}",
            new JsonObject { ["field"] = field, ["views"] = here.Select(v => (object?)v).ToList() });
    }

    /// <summary>Where every component is now, nested ones included, by name.</summary>
    public static Dictionary<string, Transform> Places(ISwAssembly asm) =>
        asm.Components(false).Select(asm.Read).ToDictionary(i => i.Name, i => i.Transform, StringComparer.OrdinalIgnoreCase);

    /// <summary>A view as results report it; steps only when they were read.</summary>
    public static ExplodedViewInfo Describe(ISwAssembly asm, string view, string configuration, string? shown, IReadOnlyList<ExplodeStepFacts>? steps) => new ExplodedViewInfo
    {
        Name = view,
        Configuration = configuration,
        Exploded = string.Equals(view, shown, StringComparison.OrdinalIgnoreCase),
        Steps = steps?.Select(s => new ExplodeStepInfo
        {
            Name = s.Name,
            Components = s.Components.Select(c => asm.Identify(c).Name).ToList(),
            DistanceMm = s.DistanceMm,
            AngleDeg = s.AngleDeg > 0 ? s.AngleDeg : (double?)null,
            Reversed = s.Reversed,
        }).ToList(),
    };
}

/// <summary>One explode step, resolved in the assembly.</summary>
sealed class ResolvedExplodeStep
{
    public List<(object Component, string Name)> Components { get; } = new List<(object Component, string Name)>();

    public object Direction { get; set; } = new object();

    public Vector3 Way { get; set; } = new Vector3(0, 0, 1);

    public double DistanceMm { get; set; }

    public bool Reverse { get; set; }

    public object? Axis { get; set; }

    public (Point3 Through, Vector3 Axis)? Line { get; set; }

    public double AngleDeg { get; set; }
}

/// <summary>
/// explode.create (design 455, Exploded views): a new view in the active configuration, made in one call. Every step is
/// checked before SOLIDWORKS is asked; then each is added, the view shown exploded and every component read, and the
/// step's components must have moved by the step (ExplodeGeometry) and nothing else. The assembly is then collapsed (or
/// shows what it showed before) with every component back. A failure deletes the view (rolled back).
/// </summary>
public sealed class ExplodeCreateOperation : AssemblyChangeOperation<ExplodeCreateRequest, ExplodeCreateResult>
{
    Dictionary<string, Transform> _collapsed = new Dictionary<string, Transform>();
    string? _shownBefore;

    public ExplodeCreateOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ExplodeCreateRequest request) => request.Document;

    protected override ExplodeCreateResult Change(ISwDocument doc, ISwAssembly asm, ExplodeCreateRequest request, ModelTransaction tx, IOperationContext context)
    {
        string configuration = ExplodeRequests.Configuration(doc, request.Configuration);
        var steps = Resolve(doc, asm, request);
        string? name = NameOf(asm, configuration, request.Name);

        string? shownBefore = asm.ShownExploded();
        _shownBefore = shownBefore;
        if (shownBefore != null && !asm.ShowExploded(shownBefore, false))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not collapse {shownBefore} to make the new view; nothing was made", new JsonObject { ["shown"] = shownBefore });
        _collapsed = ExplodeRequests.Places(asm);
        string? made = asm.CreateExplodedView();
        if (made == null)
        {
            if (shownBefore != null) asm.ShowExploded(shownBefore, true);
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no exploded view in {configuration}", new JsonObject { ["configuration"] = configuration });
        }
        string view = made;
        var created = new CreatedItem { Name = view, Kind = ReferenceKind.Feature };
        tx.Made(created, () =>
        {
            if (string.Equals(asm.ShownExploded(), view, StringComparison.OrdinalIgnoreCase)) asm.ShowExploded(view, false);
            if (!asm.DeleteExplodedView(view, out string deleted))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not delete the exploded view {view}; tried {deleted}", new JsonObject { ["view"] = view, ["tried"] = deleted });
            if (shownBefore != null) asm.ShowExploded(shownBefore, true);
        });

        var outcomes = new List<ExplodeStepOutcome>();
        var tried = new List<string>();
        var before = _collapsed;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            string? step = asm.AddExplodeStep(new ExplodeStepSpec
            {
                Components = s.Components.Select(c => c.Component).ToList(), Direction = s.Direction, DistanceMm = s.DistanceMm, Reverse = s.Reverse,
                RotationAxis = s.Axis, AngleDeg = s.AngleDeg,
            }, out string how);
            tried.Add($"step {i + 1}: {how}");
            context.Log.Info($"{Name}: step {i + 1} of {view}: {how}");
            if (step == null)
            {
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS made no step {i + 1} of {view}. Tried: {how}",
                    new JsonObject { ["field"] = $"/steps/{i}", ["tried"] = how });
            }
            var readings = ExplodeGeometry.Step(before, s.Components.Select(c => c.Name).ToList(), s.Way, s.DistanceMm, s.Line, s.AngleDeg);
            if (!asm.ShowExploded(view, true))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not show {view} exploded to check step {i + 1}", new JsonObject { ["field"] = $"/steps/{i}" });
            var after = ExplodeRequests.Places(asm);
            var (reading, moved) = ExplodeGeometry.Match(i, readings, before, after);
            outcomes.Add(new ExplodeStepOutcome { Index = i, Name = step, Sense = reading.Sense, TurnSense = reading.TurnSense, Components = moved });
            before = after;
        }

        if (name != null && !string.Equals(name, view, StringComparison.Ordinal))
        {
            if (!asm.RenameExplodedView(view, name, out string renamed))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not rename {view} to {name}; tried {renamed}", new JsonObject { ["field"] = "/name", ["tried"] = renamed });
            view = name;
            created.Name = name;
        }
        if (string.Equals(asm.ShownExploded(), view, StringComparison.OrdinalIgnoreCase) && !asm.ShowExploded(view, false))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not collapse {view} after making it");
        ExplodeGeometry.Back(_collapsed, ExplodeRequests.Places(asm));
        if (shownBefore != null && !asm.ShowExploded(shownBefore, true))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not show {shownBefore} exploded again, as it was before");
        context.Log.Info($"{Name}: {view} in {configuration}, {outcomes.Count} steps ({string.Join(", ", outcomes.Select(o => o.Sense + (o.TurnSense == null ? "" : "/" + o.TurnSense)))}); {string.Join("; ", tried)}");
        return new ExplodeCreateResult
        {
            View = new ExplodedViewInfo { Name = view, Configuration = configuration },
            Steps = outcomes,
            Tried = string.Join("; ", tried),
        };
    }

    /// <summary>The view is there with every step, holding its components, and the assembly shows what it showed before.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, ExplodeCreateRequest request, ExplodeCreateResult result)
    {
        string view = result.View.Name, configuration = result.View.Configuration;
        if (!asm.ExplodedViews(configuration).Contains(view, StringComparer.OrdinalIgnoreCase))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported {view} made, but {configuration} has no such exploded view",
                new JsonObject { ["check"] = "view_exists" });
        }
        var steps = asm.ExplodeSteps(view);
        if (steps.Count != request.Steps.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{view} has {steps.Count} steps, not {request.Steps.Count}",
                new JsonObject { ["check"] = "steps", ["expected"] = request.Steps.Count, ["measured"] = steps.Count });
        }
        for (int i = 0; i < steps.Count; i++)
        {
            int asked = result.Steps[i].Components.Count;
            if (steps[i].Components.Count != asked)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"step {i + 1} of {view} moves {steps[i].Components.Count} components, not {asked}",
                    new JsonObject { ["check"] = "step_components", ["field"] = $"/steps/{i}", ["expected"] = asked, ["measured"] = steps[i].Components.Count });
            }
        }
        string? shown = asm.ShownExploded();
        if (!string.Equals(shown, _shownBefore, StringComparison.OrdinalIgnoreCase))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the assembly shows {shown ?? "no view"} exploded, not {_shownBefore ?? "no view"} as before",
                new JsonObject { ["check"] = "shown", ["expected"] = _shownBefore, ["measured"] = shown });
        }
        if (_shownBefore == null) ExplodeGeometry.Back(_collapsed, ExplodeRequests.Places(asm));
        result.View = ExplodeRequests.Describe(asm, view, configuration, shown, steps);
    }

    /// <summary>Every step checked, its components and entities resolved, before SOLIDWORKS is asked.</summary>
    List<ResolvedExplodeStep> Resolve(ISwDocument doc, ISwAssembly asm, ExplodeCreateRequest request)
    {
        if (request.Steps.Count == 0 || request.Steps.Count > 20)
            throw PatternRequests.Invalid("/steps", $"steps must hold 1 to 20 steps, not {request.Steps.Count}");
        var list = new List<ResolvedExplodeStep>();
        for (int i = 0; i < request.Steps.Count; i++)
        {
            var step = request.Steps[i];
            string at = $"/steps/{i}";
            var r = new ResolvedExplodeStep();
            if (step.Components.Count == 0) throw PatternRequests.Invalid($"{at}/components", $"step {i + 1} names no components; name at least one to move");
            for (int j = 0; j < step.Components.Count; j++)
            {
                string field = $"{at}/components/{j}";
                var component = Component(doc, asm, step.Components[j], field);
                var info = asm.Read(component);
                if (info.Parent != null)
                    throw PatternRequests.Invalid(field, $"{info.Name} is inside the subassembly {info.Parent}; explode {info.Parent} itself, or explode the component in {info.Parent}'s own document");
                if (info.State != ComponentState.Resolved)
                {
                    throw new SwAgentException(ErrorCode.InvalidArgument,
                        $"{info.Name} is {WireNames.ForEnum(info.State)}, so it cannot be moved; {ReferenceService.Remedy(new ComponentIdentity(info.Name, info.Path, info.State))}",
                        new JsonObject { ["field"] = field, ["component"] = info.Name, ["state"] = WireNames.ForEnum(info.State) });
                }
                if (!r.Components.Exists(c => string.Equals(c.Name, info.Name, StringComparison.OrdinalIgnoreCase))) r.Components.Add((component, info.Name));
            }
            var direction = PatternEntities.Resolve(Services.References, doc, ExplodeRequests.Direction(step.Direction), step.DirectionComponent, $"{at}/direction", $"{at}/direction_component");
            r.Direction = direction.Entity;
            r.Way = PatternGeometry.DirectionOf(direction.Info) ?? throw PatternRequests.Invalid($"{at}/direction",
                $"{at}/direction names a {WireNames.ForEnum(direction.Info.Geometry)} {WireNames.ForEnum(direction.Info.Kind)}; a step moves along x, y, z, a straight edge, an axis, or a plane's or planar face's normal");
            if (step.DistanceMm == null) throw PatternRequests.Invalid($"{at}/distance_mm", $"{at}/distance_mm is needed: how far to move the components, mm");
            if (!(step.DistanceMm > 0 && step.DistanceMm <= 10_000)) throw PatternRequests.Invalid($"{at}/distance_mm", $"{at}/distance_mm must be more than 0 and at most 10000 mm, not {step.DistanceMm}");
            r.DistanceMm = step.DistanceMm.Value;
            r.Reverse = step.Reverse == true;
            // Plan 4.E live run: on SOLIDWORKS 2026 SP4, AddExplodeStep2 with an angle makes two steps and turns the components
            // about their own origins or the assembly's, whichever axis is selected, never about the axis asked; so a turn is refused.
            if (step.RotateAbout != null || step.AngleDeg != null)
                throw PatternRequests.Invalid($"{at}/rotate_about", RotateRefused);
            list.Add(r);
        }
        return list;
    }

    /// <summary>Why rotate_about and angle_deg are refused (plan 4.E live run).</summary>
    public const string RotateRefused = "SOLIDWORKS 2026 SP4 does not turn explode steps about a chosen axis through its API (it turns them about their own origins, and makes two steps); leave out rotate_about and angle_deg, and turn components with component.place";

    /// <summary>The name asked for, unless it is blank or another view of the configuration has it; null when none is asked.</summary>
    static string? NameOf(ISwAssembly asm, string configuration, string? name)
    {
        if (name == null) return null;
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 100) throw PatternRequests.Invalid("/name", "name must be 1 to 100 characters, not blank");
        if (asm.ExplodedViews(configuration).Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            throw PatternRequests.Invalid("/name", $"{configuration} already has an exploded view named {trimmed}; choose another name, or delete that view with explode.delete");
        return trimmed;
    }
}

/// <summary>
/// explode.list (design 455, Exploded views): read-only. Every configuration's views; steps for the active one's, which
/// SOLIDWORKS gives only while a view is shown (ISwAssembly.ExplodeSteps shows it and puts back what was shown).
/// </summary>
public sealed class ExplodeListOperation : Operation<ExplodeListRequest, ExplodeListResult>
{
    readonly AddInServices _services;

    public ExplodeListOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExplodeListResult Run(ExplodeListRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        string active = doc.ActiveConfiguration();
        string? shown = asm.ShownExploded();
        var result = new ExplodeListResult { Document = doc.Info(), Configuration = active, Exploded = shown };
        foreach (var configuration in doc.ConfigurationNames())
        {
            bool isActive = string.Equals(configuration, active, StringComparison.OrdinalIgnoreCase);
            foreach (var view in asm.ExplodedViews(configuration))
                result.Views.Add(ExplodeRequests.Describe(asm, view, configuration, isActive ? shown : null, isActive ? asm.ExplodeSteps(view) : null));
        }
        return result;
    }

    /// <summary>Reading changed nothing: the assembly shows what it showed before.</summary>
    protected override void Verify(ExplodeListRequest request, ExplodeListResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        string? shown = asm.ShownExploded();
        if (!string.Equals(shown, result.Exploded, StringComparison.OrdinalIgnoreCase))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"reading the views left {shown ?? "no view"} shown exploded, not {result.Exploded ?? "no view"} as before",
                new JsonObject { ["check"] = "shown", ["expected"] = result.Exploded, ["measured"] = shown });
        }
    }
}

/// <summary>
/// explode.show (design 455, Exploded views): explodes the assembly to a view of the active configuration, or collapses
/// it. Display state only, so not a transaction; it waits like a change does, since the Explode PropertyManager is a
/// command and an in-context edit is the user's.
/// </summary>
public sealed class ExplodeShowOperation : Operation<ExplodeShowRequest, ExplodeShowResult>
{
    readonly AddInServices _services;

    public ExplodeShowOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExplodeShowResult Run(ExplodeShowRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        AssemblyChangeGuard.Require(_services.SolidWorks, doc, asm, Name);
        string view = ExplodeRequests.View(doc, asm, request.View);
        bool exploded = request.Exploded ?? true;
        string? before = asm.ShownExploded();
        if (exploded && !string.Equals(before, view, StringComparison.OrdinalIgnoreCase) && !asm.ShowExploded(view, true))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not show {view} exploded", new JsonObject { ["view"] = view });
        if (!exploded && before != null && !asm.ShowExploded(before, false))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not collapse {before}", new JsonObject { ["view"] = before });
        return new ExplodeShowResult { Document = doc.Info(), View = view, Exploded = exploded, Before = before };
    }

    /// <summary>The assembly shows the view exploded, or no view, as asked.</summary>
    protected override void Verify(ExplodeShowRequest request, ExplodeShowResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        string? shown = asm.ShownExploded();
        string? wanted = result.Exploded ? result.View : null;
        if (!string.Equals(shown, wanted, StringComparison.OrdinalIgnoreCase))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the assembly shows {shown ?? "no view"} exploded, not {wanted ?? "no view"}",
                new JsonObject { ["check"] = "shown", ["expected"] = wanted, ["measured"] = shown });
        }
    }
}

/// <summary>
/// explode.delete (design 455, Exploded views): a view of the active configuration and its steps. A view shown exploded
/// is collapsed first, recorded so that a refused delete shows it again (and nothing is "put back" from the exploded
/// placements the change began with). A deletion is not rolled back.
/// </summary>
public sealed class ExplodeDeleteOperation : AssemblyChangeOperation<ExplodeDeleteRequest, ExplodeDeleteResult>
{
    public ExplodeDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(ExplodeDeleteRequest request) => request.Document;

    protected override ExplodeDeleteResult Change(ISwDocument doc, ISwAssembly asm, ExplodeDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        string configuration = doc.ActiveConfiguration();
        string view = ExplodeRequests.View(doc, asm, request.View);
        int steps = asm.ExplodeSteps(view).Count;
        if (string.Equals(asm.ShownExploded(), view, StringComparison.OrdinalIgnoreCase))
        {
            if (!asm.ShowExploded(view, false)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not collapse {view} to delete it");
            tx.Edited(new ChangedItem { Name = view, Property = "exploded", Before = "true", After = "false" }, () => asm.ShowExploded(view, true));
        }
        if (!asm.DeleteExplodedView(view, out string how))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not delete the exploded view {view}; tried {how}", new JsonObject { ["view"] = view, ["tried"] = how });
        context.Log.Info($"{Name}: {view} of {configuration}, {steps} steps; {how}");
        return new ExplodeDeleteResult { Deleted = view, Configuration = configuration, Steps = steps };
    }

    /// <summary>The view is gone.</summary>
    protected override void Check(ISwDocument doc, ISwAssembly asm, ExplodeDeleteRequest request, ExplodeDeleteResult result)
    {
        if (asm.ExplodedViews(result.Configuration).Contains(result.Deleted, StringComparer.OrdinalIgnoreCase))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported {result.Deleted} deleted, but {result.Configuration} still has it",
                new JsonObject { ["check"] = "view_deleted", ["remaining"] = new List<object?> { result.Deleted } });
        }
    }
}
