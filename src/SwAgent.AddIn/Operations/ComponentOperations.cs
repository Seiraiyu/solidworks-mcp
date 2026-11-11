using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

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
