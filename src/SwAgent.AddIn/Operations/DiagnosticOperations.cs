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
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "diag.editing");
        return new DiagEditingResult { Sketch = doc.EditingSketch(), Command = _services.SolidWorks.OpenCommand() };
    }

    /// <summary>Nothing to check: it only reads.</summary>
    protected override void Verify(DiagEditingRequest request, DiagEditingResult result, IOperationContext context)
    {
    }
}