using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// The real assembly, through IAssemblyDoc and IComponent2. UI thread only. Converts metres to millimetres here and in
/// <see cref="Placement"/>, nowhere else. Covered by the assemblies live suite, not by unit tests.
/// </summary>
internal sealed class SolidWorksAssembly : ISwAssembly
{
    const double Mm = 1000.0;

    readonly ISldWorks _sw;
    readonly IModelDoc2 _doc;
    readonly IAssemblyDoc _asm;

    public SolidWorksAssembly(ISldWorks sw, IModelDoc2 doc)
    {
        _sw = sw;
        _doc = doc;
        _asm = (IAssemblyDoc)doc;
    }

    public IReadOnlyList<object> Components(bool topLevelOnly)
    {
        var list = new List<object>();
        if (!(_doc.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) is IComponent2 root)) return list;
        Walk(root, topLevelOnly, list);
        return list;
    }

    static void Walk(IComponent2 parent, bool topLevelOnly, List<object> into)
    {
        if (!(parent.GetChildren() is object[] children)) return;
        foreach (var o in children)
        {
            if (!(o is IComponent2 child)) continue;
            into.Add(child);
            if (!topLevelOnly) Walk(child, false, into);
        }
    }

    public object? ComponentByName(string name) =>
        (object?)_asm.GetComponentByName(name)
        ?? Components(false).Cast<IComponent2>().FirstOrDefault(c => string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase));

    public ComponentInfo Read(object component)
    {
        var c = (IComponent2)component;
        string path = c.GetPathName();
        bool subassembly = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
        int suppression = c.GetSuppression2();
        var state = suppression switch
        {
            (int)swComponentSuppressionState_e.swComponentSuppressed => path.Length > 0 && !File.Exists(path) ? ComponentState.Missing : ComponentState.Suppressed,
            (int)swComponentSuppressionState_e.swComponentLightweight => ComponentState.Lightweight,
            (int)swComponentSuppressionState_e.swComponentFullyLightweight => ComponentState.Lightweight,
            _ => ComponentState.Resolved,
        };
        bool fixedInPlace = c.IsFixed();
        return new ComponentInfo
        {
            Name = c.Name2,
            Path = path.Length == 0 ? null : path,
            Configuration = c.ReferencedConfiguration,
            State = state,
            Fixed = fixedInPlace,
            Hidden = c.IsHidden(false),
            Transform = c.Transform2 is MathTransform xf && xf.ArrayData is double[] a ? Placement.FromArray(a) : new Transform(),
            Box = state == ComponentState.Resolved && c.GetBox(false, false) is double[] box && box.Length >= 6
                ? new Box3 { Min = new Point3(box[0] * Mm, box[1] * Mm, box[2] * Mm), Max = new Point3(box[3] * Mm, box[4] * Mm, box[5] * Mm) }
                : null,
            Subassembly = subassembly,
            SolveAs = subassembly ? (c.Solving == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? SolveAs.Flexible : SolveAs.Rigid) : (SolveAs?)null,
            Constraint = fixedInPlace ? Constraint.Fixed : MapConstraint(c.GetConstrainedStatus()),
            Parent = c.GetParent() is IComponent2 parent ? parent.Name2 : null,
            Mates = c.GetMates() is object[] mates ? mates.Length : 0,
        };
    }

    internal static Constraint MapConstraint(int status) => status switch
    {
        (int)swConstrainedStatus_e.swFullyConstrained => Constraint.FullyDefined,
        (int)swConstrainedStatus_e.swUnderConstrained => Constraint.UnderDefined,
        (int)swConstrainedStatus_e.swOverConstrained => Constraint.OverDefined,
        _ => Constraint.Unknown,
    };

    public string? EditingComponent() =>
        _asm.GetEditTarget() is IModelDoc2 target && !string.Equals(target.GetTitle(), _doc.GetTitle(), StringComparison.OrdinalIgnoreCase)
            ? target.GetTitle()
            : null;

    public int MateCount()
    {
        var group = MateGroup();
        int count = 0;
        for (var f = group?.GetFirstSubFeature() as IFeature; f != null; f = f.GetNextSubFeature() as IFeature) count++;
        return count;
    }

    IFeature? MateGroup()
    {
        for (var f = _doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
        {
            if (f.GetTypeName2() == "MateGroup") return f;
        }
        return null;
    }

    public IReadOnlyList<string> MatesOf(object component)
    {
        var names = new List<string>();
        if (!(((IComponent2)component).GetMates() is object[] mates)) return names;
        foreach (var m in mates) names.Add((m as IFeature)?.Name ?? "a mate");
        return names;
    }

    public object? Insert(string path, string? configuration, Transform at)
    {
        int type = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
        IModelDoc2? opened = null;
        if (!(_sw.GetOpenDocumentByName(path) is IModelDoc2))
        {
            // AddComponent5 needs the file in memory: load it without a window, resolved, as SOLIDWORKS does when inserting by hand.
            int errors = 0, warnings = 0;
            int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent
                | (type == (int)swDocumentTypes_e.swDocASSEMBLY ? (int)swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight : 0);
            _sw.DocumentVisible(false, type);
            try
            {
                opened = _sw.OpenDoc6(path, type, options, configuration ?? "", ref errors, ref warnings);
            }
            finally
            {
                _sw.DocumentVisible(true, type);
            }
            if (opened == null)
            {
                var names = SolidWorksCodes.LoadErrors(errors);
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"SOLIDWORKS could not load {path} to insert it: {string.Join(", ", names.DefaultIfEmpty("no error reported"))}",
                    new JsonObject { ["path"] = path, ["errors"] = names.Select(n => (object?)n).ToList() });
            }
        }
        int activateErrors = 0;
        _sw.ActivateDoc3(_doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
        var component = _asm.AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "",
            configuration != null, configuration ?? "", at.Origin.X / Mm, at.Origin.Y / Mm, at.Origin.Z / Mm);
        if (component == null)
        {
            if (opened != null) _sw.CloseDoc(opened.GetTitle());
            return null;
        }
        SetTransform(component, at);
        return component;
    }

    public void SetTransform(object component, Transform transform)
    {
        var math = (IMathUtility)_sw.GetMathUtility();
        ((IComponent2)component).Transform2 = (MathTransform)math.CreateTransform(Placement.ToArray(transform));
        _doc.GraphicsRedraw2();
    }

    public void SetFixed(object component, bool fix)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!((IComponent2)component).Select4(false, null, false))
                throw new SwAgentException(ErrorCode.ApiError, $"the component {((IComponent2)component).Name2} could not be selected to {(fix ? "fix" : "float")} it");
            if (fix) _asm.FixComponent();
            else _asm.UnfixComponent();
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public bool SetSuppression(object component, Suppression suppression) =>
        ((IComponent2)component).SetSuppression2(suppression == Suppression.Suppressed
            ? (int)swComponentSuppressionState_e.swComponentSuppressed
            : (int)swComponentSuppressionState_e.swComponentFullyResolved) == (int)swSuppressionError_e.swSuppressionChangeOk;

    public void SetConfiguration(object component, string configuration)
    {
        ((IComponent2)component).ReferencedConfiguration = configuration;
        _doc.EditRebuild3();
    }

    public void SetSolveAs(object component, SolveAs solveAs)
    {
        var c = (IComponent2)component;
        _doc.ClearSelection2(true);
        try
        {
            if (!c.Select4(false, null, false))
                throw new SwAgentException(ErrorCode.ApiError, $"the component {c.Name2} could not be selected to make it {WireNames.ForEnum(solveAs)}");
            // CompConfigProperties5 sets every property of the selected component at once: everything but solving is passed back as it is.
            bool ok = _asm.CompConfigProperties5(c.GetSuppression2(),
                solveAs == SolveAs.Flexible ? (int)swComponentSolvingOption_e.swComponentFlexibleSolving : (int)swComponentSolvingOption_e.swComponentRigidSolving,
                !c.IsHidden(false), true, c.ReferencedConfiguration, c.ExcludeFromBOM, c.IsEnvelope());
            if (!ok) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not make {c.Name2} {WireNames.ForEnum(solveAs)}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public void SetHidden(object component, bool hidden) =>
        ((IComponent2)component).Visible = hidden ? (int)swComponentVisibilityState_e.swComponentHidden : (int)swComponentVisibilityState_e.swComponentVisible;

    public void Delete(IReadOnlyList<object> components)
    {
        _doc.ClearSelection2(true);
        try
        {
            foreach (IComponent2 c in components)
            {
                if (!c.Select4(true, null, false)) throw new SwAgentException(ErrorCode.ApiError, $"the component {c.Name2} could not be selected to delete it");
            }
            if (!_doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", components.Cast<IComponent2>().Select(c => c.Name2))}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public void Rebuild() => _doc.EditRebuild3();

    public bool EditInContext(object component)
    {
        _doc.ClearSelection2(true);
        if (!((IComponent2)component).Select4(false, null, false)) return false;
        int info = 0;
        int status = _asm.EditPart2(true, false, ref info);
        _doc.ClearSelection2(true);
        return status == (int)swEditPartCommandStatus_e.swEditPartSuccessful;
    }

    public void EditAssembly() => _asm.EditAssembly();
}
