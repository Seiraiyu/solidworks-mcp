using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// An operation that changes an assembly (design 455, Assembly changes). As <see cref="ModelChangeOperation{TRequest,TResult}"/>
/// is for parts: it refuses to start while the user is in the middle of something (busy, user_editing, now including a
/// component edited in the assembly's context), records what it makes and edits in a <see cref="ModelTransaction"/>,
/// checks the change, and either returns the evidence block or leaves the assembly as it found it. Solving moves
/// components a request never named, so before the change it records where every component is, and a rollback puts back
/// every one that moved. Verify refuses new rebuild errors and newly over-defined components.
/// </summary>
public abstract class AssemblyChangeOperation<TRequest, TResult> : Operation<TRequest, TResult>
    where TRequest : class
    where TResult : class, IAssemblyChangeResult
{
    Pending? _pending;

    protected AssemblyChangeOperation(AddInServices services)
    {
        Services = services;
    }

    protected AddInServices Services { get; }

    /// <summary>The request's document field: the assembly's title or path, or null for the active document.</summary>
    protected abstract string? DocumentOf(TRequest request);

    /// <summary>Makes the change, recording in <paramref name="tx"/> everything made or edited as it is made.</summary>
    protected abstract TResult Change(ISwDocument doc, ISwAssembly asm, TRequest request, ModelTransaction tx, IOperationContext context);

    /// <summary>The operation's own exact checks, re-reading SOLIDWORKS rather than trusting what Change returned; throws when one fails.</summary>
    protected abstract void Check(ISwDocument doc, ISwAssembly asm, TRequest request, TResult result);

    /// <summary>The component <paramref name="text"/> names, by reference or name.</summary>
    protected object Component(ISwDocument doc, ISwAssembly asm, string text, string field) =>
        ComponentLookup.Find(Services.References, doc, asm, text, field);

    /// <summary>A component as results report it, with its reference.</summary>
    protected ComponentInfo Describe(ISwDocument doc, ISwAssembly asm, object component)
    {
        var info = asm.Read(component);
        info.Reference = Services.References.Issue(doc, component, doc.Describe(component));
        return info;
    }

    protected sealed override TResult Run(TRequest request, IOperationContext context)
    {
        _pending = null;
        var (doc, asm) = DocumentLocator.FindAssembly(Services.SolidWorks, DocumentOf(request), Name);
        AssemblyChangeGuard.Require(Services.SolidWorks, doc, asm, Name);
        var before = Snapshot.Take(asm);
        var errorsBefore = new HashSet<string>(doc.WhatsWrong().Where(i => !i.Warning).Select(i => i.Feature), StringComparer.Ordinal);
        var tx = new ModelTransaction();
        TResult result;
        try
        {
            result = Change(doc, asm, request, tx, context);
        }
        catch (Exception e)
        {
            throw Abort(asm, tx, before, e, verifying: false);
        }
        try
        {
            Check(doc, asm, request, result);
            result.Change = Evidence(doc, asm, tx, before);
        }
        catch (Exception e)
        {
            throw Abort(asm, tx, before, e, verifying: true);
        }
        _pending = new Pending(doc, asm, tx, before, errorsBefore);
        return result;
    }

    /// <summary>No feature has a rebuild error it did not have before, and no component became over defined.</summary>
    protected sealed override void Verify(TRequest request, TResult result, IOperationContext context)
    {
        var pending = _pending ?? throw new InvalidOperationException($"{Name} was verified without being run");
        _pending = null;
        try
        {
            var added = pending.Doc.WhatsWrong().Where(i => !i.Warning && !pending.ErrorsBefore.Contains(i.Feature)).ToList();
            if (added.Count > 0)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the change left rebuild errors: {string.Join(", ", added.Select(i => $"{i.Feature} ({i.Code})"))}",
                    new JsonObject { ["errors"] = added.Select(i => (object?)$"{i.Feature}: {i.Code}").ToList() });
            }
            var over = Snapshot.Take(pending.Asm).Components.Where(p => p.Value.Constraint == Constraint.OverDefined
                && !(pending.Before.Components.TryGetValue(p.Key, out var was) && was.Constraint == Constraint.OverDefined)).Select(p => p.Key).ToList();
            if (over.Count > 0)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"the change over-defined {string.Join(", ", over)}: their mates conflict",
                    new JsonObject { ["over_defined"] = over.Select(o => (object?)o).ToList() });
            }
        }
        catch (Exception e)
        {
            throw Abort(pending.Asm, pending.Tx, pending.Before, e, verifying: true);
        }
    }

    /// <summary>
    /// Rolls the transaction back, then puts every component that moved back where it was (top level first, since a
    /// subassembly carries its own components), and adds what it put back to the error.
    /// </summary>
    static SwAgentException Abort(ISwAssembly asm, ModelTransaction tx, Snapshot before, Exception cause, bool verifying)
    {
        var error = tx.Abort(cause, verifying);
        var (restored, failed) = before.Restore(asm);
        if (restored.Count == 0 && failed.Count == 0) return error;
        var details = new JsonObject();
        if (error.Error.Details != null)
        {
            foreach (var pair in error.Error.Details) details[pair.Key] = pair.Value;
        }
        details["positions_restored"] = restored.Select(r => (object?)r).ToList();
        if (failed.Count == 0)
            return new SwAgentException(new SwAgentError(error.Code, $"{error.Error.Message}; components moved back: {string.Join(", ", restored)}", details));
        var remaining = details.TryGetValue("remaining", out var r0) && r0 is List<object?> list ? list : new List<object?>();
        remaining.AddRange(failed.Select(f => (object?)f));
        details["remaining"] = remaining;
        details["rollback_failed"] = true;
        if (!details.ContainsKey("cause")) details["cause"] = WireNames.ForEnum(error.Code);
        return new SwAgentException(new SwAgentError(ErrorCode.OperationFailed,
            $"{error.Error.Message}; putting the components back failed too ({string.Join("; ", failed)}), so the assembly is left with unsaved changes for you to inspect (assembly.components); it was not closed or reverted",
            details));
    }

    static AssemblyChange Evidence(ISwDocument doc, ISwAssembly asm, ModelTransaction tx, Snapshot before)
    {
        var after = Snapshot.Take(asm);
        var moved = after.Components.Where(a => before.Components.TryGetValue(a.Key, out var b) && Placement.Moved(b.Transform, a.Value.Transform))
            .Select(a => new MovedComponent
            {
                Name = a.Key,
                Before = before.Components[a.Key].Transform.Origin,
                After = a.Value.Transform.Origin,
                Rotated = Placement.Rotated(before.Components[a.Key].Transform, a.Value.Transform),
            })
            .ToList();
        var touched = moved.Select(m => m.Name)
            .Concat(tx.Created.Where(c => c.Kind == ReferenceKind.Component).Select(c => c.Name))
            .Concat(tx.Changed.Select(c => c.Name))
            .Where(after.Components.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var problems = doc.WhatsWrong();
        return new AssemblyChange
        {
            Document = doc.Info(),
            Created = tx.Created.ToList(),
            Changed = tx.Changed.ToList(),
            Rebuild = new RebuildStatus
            {
                Errors = problems.Count(p => !p.Warning),
                Warnings = problems.Count(p => p.Warning),
                Items = problems.ToList(),
            },
            ComponentsBefore = before.Components.Count,
            ComponentsAfter = after.Components.Count,
            MatesBefore = before.Mates,
            MatesAfter = after.Mates,
            Moved = moved,
            Constraints = touched.Select(n => new ComponentConstraint { Name = n, Constraint = after.Components[n].Constraint }).ToList(),
        };
    }

    sealed class Pending
    {
        public Pending(ISwDocument doc, ISwAssembly asm, ModelTransaction tx, Snapshot before, HashSet<string> errorsBefore)
        {
            Doc = doc;
            Asm = asm;
            Tx = tx;
            Before = before;
            ErrorsBefore = errorsBefore;
        }

        public ISwDocument Doc { get; }
        public ISwAssembly Asm { get; }
        public ModelTransaction Tx { get; }
        public Snapshot Before { get; }
        public HashSet<string> ErrorsBefore { get; }
    }
}

/// <summary>Where a component was and how far it was defined.</summary>
public sealed class Position
{
    public Position(object component, Transform transform, Constraint constraint, bool fix, bool topLevel)
    {
        Component = component;
        Transform = transform;
        Constraint = constraint;
        Fixed = fix;
        TopLevel = topLevel;
    }

    public object Component { get; }
    public Transform Transform { get; }
    public Constraint Constraint { get; }
    public bool Fixed { get; }
    public bool TopLevel { get; }
}

/// <summary>
/// An assembly read before (or after) a change: every component's position by name, nested ones included, and the mate
/// count. Restoring it puts back every component that moved (design 455, Assembly changes).
/// </summary>
public sealed class Snapshot
{
    Snapshot(Dictionary<string, Position> components, int mates)
    {
        Components = components;
        Mates = mates;
    }

    public Dictionary<string, Position> Components { get; }

    public int Mates { get; }

    public static Snapshot Take(ISwAssembly asm)
    {
        var all = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in asm.Components(false))
        {
            var info = asm.Read(c);
            all[info.Name] = new Position(c, info.Transform, info.Constraint, info.Fixed, info.Parent == null);
        }
        return new Snapshot(all, asm.MateCount());
    }

    /// <summary>
    /// Moves every component still in the assembly that is not where it was back there, top level first (a subassembly
    /// carries its own components, so those are looked at again afterwards). What was put back, and what could not be.
    /// </summary>
    public (List<string> Restored, List<string> Failed) Restore(ISwAssembly asm)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        foreach (bool topLevel in new[] { true, false })
        {
            foreach (var c in asm.Components(topLevel))
            {
                var now = asm.Read(c);
                if (!topLevel && now.Parent == null) continue;
                if (!Components.TryGetValue(now.Name, out var was) || !Placement.Moved(was.Transform, now.Transform) || restored.Contains(now.Name)) continue;
                try
                {
                    asm.SetTransform(c, was.Transform);
                    if (Placement.Moved(was.Transform, asm.Read(c).Transform)) failed.Add($"{now.Name}: it did not go back to {was.Transform.Origin}");
                    else restored.Add(now.Name);
                }
                catch (Exception e)
                {
                    failed.Add($"{now.Name}: {(e is SwAgentException s ? s.Error.Message : e.Message)}");
                }
            }
        }
        return (restored, failed);
    }
}

/// <summary>
/// The precondition of every assembly change: phase 3's (no sketch being edited, no command open), and the user is not
/// editing a component in the assembly's context (design 455, Assembly changes). Otherwise busy with user_editing, before
/// anything is changed; nothing of the user's is closed.
/// </summary>
public static class AssemblyChangeGuard
{
    public static void Require(ISolidWorksApplication sw, ISwDocument doc, ISwAssembly asm, string operation)
    {
        ModelChangeGuard.Require(sw, doc, operation);
        string? editing = asm.EditingComponent();
        if (editing == null) return;
        throw new SwAgentException(ErrorCode.Busy,
            $"{operation} was not started: the user is editing {editing} in the context of {doc.Info().Title}. That is the user's to finish (Edit Assembly); wait for it (or ask them), then try again. Nothing was changed",
            new JsonObject { ["reason"] = "user_editing", ["detected"] = "before", ["started"] = false, ["component"] = editing });
    }
}
