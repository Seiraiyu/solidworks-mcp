using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>
/// assembly.interference (design 455, Interference): read-only. SOLIDWORKS' interference detection over the whole
/// assembly, the report kept to the interferences that involve the listed components. The session is closed (Done) in
/// a finally: on success, when SOLIDWORKS throws, and when the client's deadline passed while it ran (an operation
/// already running is never interrupted, so the finally still runs when the read returns).
/// </summary>
public sealed class AssemblyInterferenceOperation : Operation<AssemblyInterferenceRequest, AssemblyInterferenceResult>
{
    readonly AddInServices _services;

    public AssemblyInterferenceOperation(AddInServices services)
    {
        _services = services;
    }

    protected override AssemblyInterferenceResult Run(AssemblyInterferenceRequest request, IOperationContext context)
    {
        var (doc, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, request.Document, Name);
        var only = Listed(doc, asm, request.Components);
        var options = new InterferenceOptions { CoincidentIsInterference = request.CoincidentIsInterference == true, IncludeHidden = request.IncludeHidden == true };
        var session = asm.OpenInterference(options);
        _services.Runtime.InterferenceOpened();
        IReadOnlyList<InterferenceFacts> found;
        string? notClosed = null;
        try
        {
            int delay = _services.Runtime.TakeInterferenceDelay();
            if (delay > 0)
            {
                context.Log.Warn($"assembly.interference: holding the interference session open for {delay} ms (diag.interference)");
                System.Threading.Thread.Sleep(delay);
            }
            found = session.Read();
        }
        catch (COMException e)
        {
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS' interference detection failed: {e.Message}; the interference session was closed",
                new JsonObject { ["hresult"] = e.HResult });
        }
        finally
        {
            notClosed = Close(session, context);
        }
        if (notClosed != null)
        {
            throw new SwAgentException(ErrorCode.OperationFailed, $"the interferences were read, but closing SOLIDWORKS' interference session failed: {notClosed}",
                new JsonObject { ["done"] = false });
        }
        if (DateTime.UtcNow > context.DeadlineUtc)
            context.Log.Warn("assembly.interference finished after its deadline; the interference session was closed (Done)");
        var result = new AssemblyInterferenceResult { Document = doc.Info(), Components = asm.Components(false).Count };
        foreach (var f in found.OrderByDescending(f => f.VolumeMm3))
        {
            var names = f.Components.Select(c => asm.Identify(c).Name).ToList();
            if (only != null && !names.Any(n => only.Any(o => Covers(o, n)))) continue;
            result.Interferences.Add(new InterferenceInfo { Components = names, VolumeMm3 = f.VolumeMm3, Box = f.Box, Touching = f.VolumeMm3 <= 1e-6 });
        }
        result.TotalVolumeMm3 = result.Interferences.Sum(i => i.VolumeMm3);
        context.Log.Info($"assembly.interference: {result.Interferences.Count} of {found.Count} interferences reported, {result.TotalVolumeMm3:0.###} mm³; coincident {options.CoincidentIsInterference}; {session.Note}");
        return result;
    }

    /// <summary>Done, always; what went wrong when it threw (logged), or null.</summary>
    string? Close(IInterferenceSession session, IOperationContext context)
    {
        try
        {
            session.Done();
            return null;
        }
        catch (Exception e)
        {
            context.Log.Error("assembly.interference: closing the interference session (Done) failed", e);
            return e.Message;
        }
        finally
        {
            _services.Runtime.InterferenceClosed();
        }
    }

    /// <summary>The listed components' names, or null for every interference.</summary>
    List<string>? Listed(ISwDocument doc, ISwAssembly asm, List<string>? components)
    {
        if (components == null) return null;
        if (components.Count == 0)
            throw new SwAgentException(ErrorCode.InvalidArgument, "components is empty; leave it out to report every interference", new JsonObject { ["field"] = "/components" });
        var names = new List<string>();
        for (int i = 0; i < components.Count; i++) names.Add(asm.Identify(ComponentLookup.Find(_services.References, doc, asm, components[i], $"/components/{i}")).Name);
        return names;
    }

    /// <summary>A listed component covers itself and, for a subassembly, every component inside it.</summary>
    static bool Covers(string listed, string name) =>
        string.Equals(listed, name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(listed + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every component reported is still in the assembly: nothing changed it while it was read.</summary>
    protected override void Verify(AssemblyInterferenceRequest request, AssemblyInterferenceResult result, IOperationContext context)
    {
        var (_, asm) = DocumentLocator.FindAssembly(_services.SolidWorks, DocumentKeys.Of(result.Document), Name);
        var gone = result.Interferences.SelectMany(i => i.Components).Distinct().Where(n => asm.ComponentByName(n) == null).ToList();
        if (gone.Count > 0)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{string.Join(", ", gone)} left the assembly while the interferences were read; ask again");
    }
}
