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
