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
