using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// One run of SOLIDWORKS' interference detection (design 455, Interference). Opening it takes the manager and sets
/// what the request asks, after reading every property it sets; Done puts each one back and then calls
/// IInterferenceDetectionMgr.Done, so no session and no changed setting is left in the user's SOLIDWORKS. The operation
/// calls Done in a finally. UI thread only.
/// </summary>
internal sealed class SolidWorksInterference : IInterferenceSession
{
    const double Mm = 1000.0;

    IInterferenceDetectionMgr _mgr;
    readonly Action _restore;

    public SolidWorksInterference(IAssemblyDoc asm, InterferenceOptions options)
    {
        _mgr = asm.InterferenceDetectionManager
            ?? throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS gave no interference detection manager");
        bool coincidence = _mgr.TreatCoincidenceAsInterference, ignoreHidden = _mgr.IgnoreHiddenBodies, subassemblies = _mgr.TreatSubAssembliesAsComponents,
            multibody = _mgr.IncludeMultibodyPartInterferences, transparent = _mgr.MakeInterferingPartsTransparent, fasteners = _mgr.CreateFastenersFolder,
            ignored = _mgr.ShowIgnoredInterferences, transform = _mgr.UseTransform;
        _restore = () =>
        {
            _mgr.TreatCoincidenceAsInterference = coincidence;
            _mgr.IgnoreHiddenBodies = ignoreHidden;
            _mgr.TreatSubAssembliesAsComponents = subassemblies;
            _mgr.IncludeMultibodyPartInterferences = multibody;
            _mgr.MakeInterferingPartsTransparent = transparent;
            _mgr.CreateFastenersFolder = fasteners;
            _mgr.ShowIgnoredInterferences = ignored;
            _mgr.UseTransform = transform;
        };
        try
        {
            _mgr.TreatCoincidenceAsInterference = options.CoincidentIsInterference;
            _mgr.IgnoreHiddenBodies = !options.IncludeHidden;
            _mgr.TreatSubAssembliesAsComponents = false;
            _mgr.IncludeMultibodyPartInterferences = false;
            _mgr.MakeInterferingPartsTransparent = false;
            _mgr.CreateFastenersFolder = false;
            _mgr.ShowIgnoredInterferences = false;
            _mgr.UseTransform = false;
            // In process, the manager's results are the ones worked out when it was taken: with the settings above only set,
            // GetInterferences still answered as before them (plan 4.D live run, 2026 SP4: coincident faces were missing,
            // while the same calls from another process found them). So close it and take it again with them in place.
            _mgr.Done();
            _mgr = asm.InterferenceDetectionManager
                ?? throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS gave no interference detection manager the second time");
            if (_mgr.TreatCoincidenceAsInterference != options.CoincidentIsInterference)
                throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not keep the interference settings between taking its manager twice");
        }
        catch
        {
            Done();
            throw;
        }
    }

    /// <summary>IInterferenceDetectionMgr.GetInterferences: each one's components, its volume (m³ to mm³) and its body's box (m to mm).</summary>
    public IReadOnlyList<InterferenceFacts> Read()
    {
        var list = new List<InterferenceFacts>();
        if (!(_mgr.GetInterferences() is object[] all)) return list;
        foreach (var i in all.OfType<IInterference>())
        {
            var facts = new InterferenceFacts { VolumeMm3 = i.Volume * 1e9 };
            if (i.Components is object[] components) facts.Components.AddRange(components.OfType<IComponent2>());
            if (i.GetInterferenceBody() is IBody2 body && body.GetBodyBox() is double[] b && b.Length >= 6)
                facts.Box = new Box3 { Min = new Point3(b[0] * Mm, b[1] * Mm, b[2] * Mm), Max = new Point3(b[3] * Mm, b[4] * Mm, b[5] * Mm) };
            list.Add(facts);
        }
        return list;
    }

    public void Done()
    {
        try
        {
            _restore();
        }
        finally
        {
            _mgr.Done();
        }
    }
}
