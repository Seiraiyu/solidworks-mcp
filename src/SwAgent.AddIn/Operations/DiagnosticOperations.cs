using System;
using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// diag.scratch_part: a dirty part the live suite owns, so a fault case can show what a crash reports about unsaved
/// work. Only under the live suite's scratch directory, only a new file, only with diagnostics on.
/// </summary>
public sealed class DiagScratchPartOperation : Operation<DiagScratchPartRequest, DocumentInfo>
{
    readonly AddInServices _services;

    public DiagScratchPartOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentInfo Run(DiagScratchPartRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (!Path.IsPathRooted(request.Path) || !LiveScratch.IsScratchPart(request.Path))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"path must be a full .SLDPRT path under {LiveScratch.Root}, not '{request.Path}'",
                new JsonObject { ["field"] = "path" });
        }
        string path = Path.GetFullPath(request.Path);
        if (File.Exists(path))
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{path} already exists; diag.scratch_part only creates new files", new JsonObject { ["field"] = "path" });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        context.Log.Warn($"diag.scratch_part: creating a dirty part at {path}");
        return _services.SolidWorks.CreateScratchPart(path);
    }

    protected override void Verify(DiagScratchPartRequest request, DocumentInfo result, IOperationContext context)
    {
        if (!string.Equals(result.Path, Path.GetFullPath(request.Path), StringComparison.OrdinalIgnoreCase) || !result.Dirty)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS reported the new part as '{result.Path}' (unsaved changes: {result.Dirty}), not a dirty part at {request.Path}");
        }
    }
}

/// <summary>
/// diag.scratch_block: a part with one block of known size, so the parts live suite can check inspection and references
/// against exact geometry before the feature operations exist. Only under the live suite's scratch directory, only a
/// new file, only with diagnostics on.
/// </summary>
public sealed class DiagScratchBlockOperation : Operation<DiagScratchBlockRequest, DocumentInfo>
{
    const double MaxMm = 1000;

    readonly AddInServices _services;

    public DiagScratchBlockOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DocumentInfo Run(DiagScratchBlockRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        if (!Path.IsPathRooted(request.Path) || !LiveScratch.IsScratchPart(request.Path))
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"path must be a full .SLDPRT path under {LiveScratch.Root}, not '{request.Path}'",
                new JsonObject { ["field"] = "path" });
        }
        foreach (var (field, value) in new[] { ("x_mm", request.XMm), ("y_mm", request.YMm), ("z_mm", request.ZMm) })
        {
            if (!(value > 0 && value <= MaxMm))
                throw new SwAgentException(ErrorCode.InvalidArgument, $"{field} must be more than 0 and at most {MaxMm} mm", new JsonObject { ["field"] = field });
        }
        string path = Path.GetFullPath(request.Path);
        if (File.Exists(path))
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{path} already exists; diag.scratch_block only creates new files", new JsonObject { ["field"] = "path" });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        context.Log.Warn($"diag.scratch_block: creating a {request.XMm} x {request.YMm} x {request.ZMm} mm block at {path}");
        var block = _services.SolidWorks.CreateScratchBlock(path, request.XMm, request.YMm, request.ZMm);
        // Created by the client, so the client may close it (document.close), as a part from document.new.
        _services.Runtime.DocumentOpened(DocumentKeys.Of(block), owned: true);
        return block;
    }

    /// <summary>The part is at the path, and its one body has the block's volume.</summary>
    protected override void Verify(DiagScratchBlockRequest request, DocumentInfo result, IOperationContext context)
    {
        string path = Path.GetFullPath(request.Path);
        if (!string.Equals(result.Path, path, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS reported the block as '{result.Path}', not saved at {path}");
        var doc = DocumentLocator.Find(_services.SolidWorks, path);
        double expected = request.XMm * request.YMm * request.ZMm;
        double volume = doc.Mass()?.VolumeMm3 ?? 0;
        if (doc.SolidBodyCount() != 1 || Math.Abs(volume - expected) > 1e-6 * expected)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the block has {doc.SolidBodyCount()} bodies and {volume} mm³, not one body of {expected} mm³");
        }
    }
}

/// <summary>Live testing only: what the model-change precondition reads, without changing anything, so the parts suite only probes with a change once the user's sketch or command is seen.</summary>
public sealed class DiagEditingOperation : Operation<DiagEditingRequest, DiagEditingResult>
{
    readonly AddInServices _services;

    public DiagEditingOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DiagEditingResult Run(DiagEditingRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        // A drawing as its change guard reads it (DrawingChangeGuard); anything else must be a part, as before.
        var doc = DocumentLocator.Find(_services.SolidWorks, request.Document);
        if (doc.Drawing() is ISwDrawing drawing)
            return new DiagEditingResult { Sketch = doc.EditingSketch(), Command = _services.SolidWorks.OpenCommand(), SheetFormat = drawing.EditingSheetFormat() };
        var part = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "diag.editing");
        return new DiagEditingResult { Sketch = part.EditingSketch(), Command = _services.SolidWorks.OpenCommand() };
    }

    /// <summary>Nothing to check: it only reads.</summary>
    protected override void Verify(DiagEditingRequest request, DiagEditingResult result, IOperationContext context)
    {
    }
}

/// <summary>
/// Live testing only: a sketch open for editing as the user would leave it, so the fault suite proves a model change
/// waits (busy, user_editing) with no one at SOLIDWORKS. Opening never sets the sketch manager's drawing options (it is
/// not the add-in's own drawing), so nothing is left changed when it is exited.
/// </summary>
/// <summary>diag.edit_component: the user's in-context edit, made on purpose for the fault suite (design 455, Testing).</summary>
public sealed class DiagEditComponentOperation : Operation<DiagEditComponentRequest, DiagEditComponentResult>
{
    readonly AddInServices _services;

    public DiagEditComponentOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DiagEditComponentResult Run(DiagEditComponentRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, "diag.edit_component");
        if (request.Editing)
        {
            if (request.Component == null) throw new SwAgentException(ErrorCode.InvalidArgument, "component: name the component to edit", new JsonObject { ["field"] = "component" });
            var component = ComponentLookup.Find(_services.References, doc, asm, request.Component, "component");
            context.Log.Warn($"diag.edit_component: editing {request.Component} in context as the user would");
            if (!asm.EditInContext(component)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not start editing {request.Component} in context");
        }
        else asm.EditAssembly();
        return new DiagEditComponentResult { Editing = asm.EditingComponent() };
    }

    /// <summary>A component is being edited in context (or none is), read again.</summary>
    protected override void Verify(DiagEditComponentRequest request, DiagEditComponentResult result, IOperationContext context)
    {
        if ((result.Editing != null) != request.Editing)
            throw new SwAgentException(ErrorCode.VerificationFailed, request.Editing ? $"{request.Component} is not being edited in context" : $"{result.Editing} is still being edited in context");
    }
}

public sealed class DiagEditSketchOperation : Operation<DiagEditSketchRequest, DiagEditingResult>
{
    readonly AddInServices _services;

    public DiagEditSketchOperation(AddInServices services)
    {
        _services = services;
    }

    protected override DiagEditingResult Run(DiagEditSketchRequest request, IOperationContext context)
    {
        if (!_services.Runtime.DiagnosticsEnabled)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                @"diagnostic operations are off; set HKCU\Software\solidworks-mcp\Diagnostics (DWORD) to 1 and reload the add-in to use them");
        }
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "diag.edit_sketch");
        if (request.Open)
        {
            if (request.Sketch == null) throw new SwAgentException(ErrorCode.InvalidArgument, "sketch: name the sketch to open", new JsonObject { ["field"] = "sketch" });
            var (sketch, _) = _services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
            context.Log.Warn($"diag.edit_sketch: opening {request.Sketch} for editing as the user would");
            if (!doc.OpenForEditing(sketch)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not open {request.Sketch} for editing");
        }
        else doc.ExitSketch();
        return new DiagEditingResult { Sketch = doc.EditingSketch(), Command = _services.SolidWorks.OpenCommand() };
    }

    /// <summary>The sketch is open (or none is), read again.</summary>
    protected override void Verify(DiagEditSketchRequest request, DiagEditingResult result, IOperationContext context)
    {
        bool open = result.Sketch != null;
        if (open != request.Open)
            throw new SwAgentException(ErrorCode.VerificationFailed, request.Open ? $"{request.Sketch} is not open for editing" : $"{result.Sketch} is still open for editing");
    }
}