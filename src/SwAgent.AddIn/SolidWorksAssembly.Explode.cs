using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Exploded views (plan 4.E). A view is made with IAssemblyDoc.CreateExplodedView and its steps with
/// IConfiguration.AddExplodeStep2, which acts on the selection: the components, the direction and the rotation axis are
/// selected with marks, never set through IExplodeStep.SetComponents, a SAFEARRAY-of-dispatch setter of the kind that
/// crashed SOLIDWORKS 2026 SP4 in plan 4.B. SOLIDWORKS' help gives no marks for it, so AddExplodeStep tries a short list
/// of schemes and keeps the first that makes a step holding every component; tried says how each went, and the live run
/// (plan 4.E, Task 14) records the one SOLIDWORKS takes. A view's steps are read through IConfiguration while it is shown
/// exploded, and what was shown before is shown again.
/// </summary>
internal sealed partial class SolidWorksAssembly
{
    static readonly (string Label, int Components, int Direction, int Axis)[] ExplodeMarks =
    {
        ("components mark 1, direction mark 2, rotation axis mark 4", 1, 2, 4),
        ("components mark 0, direction mark 2, rotation axis mark 4", 0, 2, 4),
    };

    IConfiguration ActiveConfig() => (IConfiguration)_doc.ConfigurationManager.ActiveConfiguration;

    static List<string> Strings(object? o) => o switch
    {
        string[] s => s.ToList(),
        object[] a => a.OfType<string>().ToList(),
        _ => new List<string>(),
    };

    public IReadOnlyList<string> ExplodedViews(string configuration) => Strings(_asm.GetExplodedViewNames2(configuration));

    public string? ShownExploded() => _doc.Extension.IsExploded(out string view) && !string.IsNullOrEmpty(view) ? view : null;

    public bool ShowExploded(string view, bool exploded) => _asm.ShowExploded2(exploded, view);

    public IReadOnlyList<ExplodeStepFacts> ExplodeSteps(string view)
    {
        string? shown = ShownExploded();
        bool switched = !string.Equals(shown, view, StringComparison.OrdinalIgnoreCase);
        if (switched && !_asm.ShowExploded2(true, view))
            throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS would not show the exploded view {view} to read its steps");
        try
        {
            var config = ActiveConfig();
            var steps = new List<ExplodeStepFacts>();
            int count = config.GetNumberOfExplodeSteps();
            for (int i = 0; i < count; i++)
            {
                if (!(config.GetExplodeStep(i) is IExplodeStep s)) continue;
                var facts = new ExplodeStepFacts
                {
                    Name = s.Name, DistanceMm = s.ExplodeDistance * Mm, AngleDeg = s.RotationAngle * 180 / Math.PI, Reversed = s.ReverseTranslationDirection,
                };
                for (int c = 0; c < s.GetNumOfComponents(); c++)
                {
                    if (s.GetComponent(c) is IComponent2 component) facts.Components.Add(component);
                }
                steps.Add(facts);
            }
            return steps;
        }
        finally
        {
            if (switched)
            {
                if (shown != null) _asm.ShowExploded2(true, shown);
                else _asm.ShowExploded2(false, view);
            }
        }
    }

    public string? CreateExplodedView()
    {
        string config = ActiveConfig().Name;
        var before = ExplodedViews(config);
        if (!_asm.CreateExplodedView()) return null;
        return ExplodedViews(config).FirstOrDefault(v => !before.Contains(v, StringComparer.OrdinalIgnoreCase));
    }

    public string? AddExplodeStep(ExplodeStepSpec s, out string tried)
    {
        var attempts = new List<string>();
        var config = ActiveConfig();
        foreach (var (label, components, direction, axis) in ExplodeMarks)
        {
            int before = config.GetNumberOfExplodeSteps();
            object? made;
            int error;
            _doc.ClearSelection2(true);
            try
            {
                foreach (var c in s.Components) Pick(c, components);
                Pick(s.Direction, direction);
                if (s.RotationAxis != null) Pick(s.RotationAxis, axis);
                made = config.AddExplodeStep2(s.DistanceMm / Mm, (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis, s.Reverse,
                    s.AngleDeg * Math.PI / 180, (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis, false, false, false, out error);
            }
            catch (Exception e) when (e is COMException || e is SwAgentException)
            {
                attempts.Add($"AddExplodeStep2, {label}: {e.Message}");
                continue;
            }
            finally
            {
                _doc.ClearSelection2(true);
            }
            if (made is IExplodeStep step && error == 0 && step.GetNumOfComponents() == s.Components.Count)
            {
                attempts.Add($"AddExplodeStep2, {label}: made {step.Name}");
                tried = string.Join("; ", attempts);
                return step.Name;
            }
            string what = made is IExplodeStep partial ? $"made {partial.Name} with {partial.GetNumOfComponents()} of {s.Components.Count} components" : "returned null";
            attempts.Add($"AddExplodeStep2, {label}: {what}, {ExplodeStepError(error)}; deleted what it made");
            // Anything an attempt made that is not the step asked for goes again, so the next attempt starts clean.
            int now = config.GetNumberOfExplodeSteps();
            for (int i = now - 1; i >= before; i--)
            {
                if (config.GetExplodeStep(i) is IExplodeStep stray) config.DeleteExplodeStep(stray.Name);
            }
        }
        tried = string.Join("; ", attempts);
        return null;
    }

    static string ExplodeStepError(int code) =>
        Enum.IsDefined(typeof(swCreateExplodeStepError_e), code) ? ((swCreateExplodeStepError_e)code).ToString() : $"swCreateExplodeStepError {code}";

    public bool RenameExplodedView(string view, string name)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!_doc.Extension.SelectByID2(view, "EXPLODEVIEWS", 0, 0, 0, false, 0, null, 0)) return false;
            if (!(((ISelectionMgr)_doc.SelectionManager).GetSelectedObject6(1, -1) is IFeature f)) return false;
            f.Name = name;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public bool DeleteExplodedView(string view)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!_doc.Extension.SelectByID2(view, "EXPLODEVIEWS", 0, 0, 0, false, 0, null, 0)) return false;
            return _doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }
}
