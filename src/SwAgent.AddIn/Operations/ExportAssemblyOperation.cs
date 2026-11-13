using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// export.assembly (design 455, Export): a copy of an assembly through the exporter export.part uses, with STL written as
/// one file of the whole assembly, and a STEP file proven to hold one occurrence per resolved component. A lightweight
/// component, whose geometry is not loaded, is refused before anything is written.
/// </summary>
public sealed class ExportAssemblyOperation : Operation<ExportAssemblyRequest, ExportAssemblyResult>
{
    readonly AddInServices _services;

    public ExportAssemblyOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ExportAssemblyResult Run(ExportAssemblyRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        string path = PathRules.Writable(request.Path, "path", request.Overwrite == true, ExportSettings.Extensions(request.Format));
        var settings = ExportSettings.ForAssembly(request.Format, request.Options);
        var before = doc.Info();
        var (components, bodies) = Covered(asm, before);
        if (bodies == 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{before.Title} has no resolved, visible component with a solid body to export",
                new JsonObject { ["field"] = "document", ["document"] = DocumentLocator.Label(before) });
        }
        var o = Exporter.Write(_services, doc, Name, path, request.Format, ExportSettings.SchemaOf(request.Options), settings, bodies, context);
        int? occurrences = null;
        if (request.Format == ExportFormat.Step)
        {
            occurrences = ExportFileCheck.StepOccurrences(path);
            if (occurrences != components)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    $"{path} holds {occurrences} NEXT_ASSEMBLY_USAGE_OCCURRENCE entities, not one for each of the assembly's {components} resolved components; the file is left where it was written",
                    new JsonObject { ["check"] = "step_structure", ["path"] = path, ["expected"] = components, ["measured"] = occurrences });
            }
            o.File.Checks.Add($"holds {occurrences} NEXT_ASSEMBLY_USAGE_OCCURRENCE, one per resolved component");
        }
        if (request.Format == ExportFormat.Parasolid) o.File.Checks.Add("the Parasolid file's assembly structure is not read; only its header is checked");
        return new ExportAssemblyResult
        {
            Document = o.After,
            Path = path,
            Format = request.Format,
            Bytes = o.File.Bytes,
            Header = o.File.Header,
            Components = components,
            StepOccurrences = occurrences,
            Triangles = o.File.Triangles,
            StepSchema = o.File.Schema,
            Preferences = settings.Select(s => s.ToString()).ToList(),
            Warnings = o.Warnings,
            Checks = o.File.Checks.ToList(),
        };
    }

    /// <summary>
    /// The resolved components, nested ones included (what the export covers), and the solid bodies of the visible part
    /// components among them (an STL's floor of triangles). A lightweight component is invalid_argument naming it.
    /// </summary>
    static (int Components, int Bodies) Covered(ISwAssembly asm, DocumentInfo assembly)
    {
        int components = 0, bodies = 0;
        var lightweight = new List<string>();
        foreach (var c in asm.Components(false))
        {
            var info = asm.Read(c);
            if (info.State == ComponentState.Lightweight) lightweight.Add(info.Name);
            if (info.State != ComponentState.Resolved) continue;
            components++;
            if (!info.Subassembly && !info.Hidden) bodies += asm.ModelOf(c)?.SolidBodyCount() ?? 0;
        }
        if (lightweight.Count > 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{string.Join(", ", lightweight)} {(lightweight.Count == 1 ? "is" : "are")} lightweight in {assembly.Title}: the geometry is not loaded, so it cannot be exported. Resolve it with component.set {{suppression: resolved}} first",
                new JsonObject { ["field"] = "document", ["components"] = lightweight.Select(n => (object?)n).ToList() });
        }
        return (components, bodies);
    }

    protected override void Verify(ExportAssemblyRequest request, ExportAssemblyResult result, IOperationContext context)
    {
        if (!File.Exists(result.Path))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Path} is gone after export.assembly wrote it",
                new JsonObject { ["check"] = "file_written", ["path"] = result.Path });
        }
        var (doc, _) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentLocator.Label(result.Document), Name);
        ExportPartOperation.Unchanged(result.Document, doc.Info());
    }
}
