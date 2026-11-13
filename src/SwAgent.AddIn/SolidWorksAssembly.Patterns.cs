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
/// Component patterns and interference (plan 4.D). Patterns are made by selecting with marks and calling the
/// selection-based IFeatureManager methods, never by setting SeedComponentArray: that is a SAFEARRAY-of-dispatch setter,
/// the kind that crashed SOLIDWORKS 2026 SP4 in plan 4.B, and SOLIDWORKS' own help disagrees on the marks. So each
/// method tries a short list of mark schemes and keeps the first that makes a pattern of the right kind; tried says how
/// each went, and the live run (plan 4.D, Task 13) records which one SOLIDWORKS takes.
/// </summary>
internal sealed partial class SolidWorksAssembly
{
    static readonly (string Label, int Seeds, int Direction, int Direction2)[] LinearMarks =
    {
        ("seeds mark 1, directions marks 2 and 4", 1, 2, 4),
        ("directions marks 1 and 2, seeds mark 4", 4, 1, 2),
    };

    static readonly (string Label, int Seeds, int Axis)[] CircularMarks =
    {
        ("seeds mark 1, axis mark 2", 1, 2),
        ("axis mark 1, seeds mark 4", 4, 1),
    };

    public IReadOnlyList<object> Patterns()
    {
        var list = new List<object>();
        for (var f = _doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
        {
            if (f.GetTypeName2() is string type && type.IndexOf("Pattern", StringComparison.OrdinalIgnoreCase) >= 0 && KindOf(f) != null) list.Add(f);
        }
        return list;
    }

    public object? PatternByName(string name) =>
        Patterns().Cast<IFeature>().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    static ComponentPatternKind? KindOf(IFeature f)
    {
        try
        {
            return f.GetDefinition() switch
            {
                ILocalLinearPatternFeatureData _ => ComponentPatternKind.Linear,
                ILocalCircularPatternFeatureData _ => ComponentPatternKind.Circular,
                IDerivedPatternFeatureData _ => ComponentPatternKind.Feature,
                _ => (ComponentPatternKind?)null,
            };
        }
        catch (COMException)
        {
            return null;
        }
    }

    public ComponentPatternFacts ReadPattern(object pattern)
    {
        var f = (IFeature)pattern;
        object definition = f.GetDefinition();
        int error = f.GetErrorCode2(out bool warning);
        var facts = new ComponentPatternFacts
        {
            Name = f.Name,
            Kind = KindOf(f) ?? throw new SwAgentException(ErrorCode.ApiError, $"{f.Name} is a {f.GetTypeName2()}, not a component pattern"),
            Error = error == 0 || warning ? null : SolidWorksCodes.FeatureError(error),
        };
        var seeds = Seeds(definition);
        facts.SeedsFrom = "SeedComponentArray";
        if (seeds.Count == 0)
        {
            facts.SeedsFrom = "SeedComponentArray after AccessSelections";
            // Some definitions give their selections only between AccessSelections and ReleaseSelectionAccess.
            Access(definition, true);
            try
            {
                seeds = Seeds(definition);
            }
            finally
            {
                Access(definition, false);
            }
        }
        if (seeds.Count == 0)
        {
            // The definition gave none: take the components among the feature's parents.
            facts.SeedsFrom = "GetParents";
            seeds = (f.GetParents() as object[] ?? Array.Empty<object>()).OfType<IFeature>()
                .Select(p => p.GetSpecificFeature2() as IComponent2).Where(c => c != null).Cast<IComponent2>().ToList();
        }
        facts.Seeds.AddRange(seeds);
        var seedNames = new HashSet<string>(seeds.Select(s => s.Name2), StringComparer.OrdinalIgnoreCase);
        for (var sub = f.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
        {
            if (sub.GetSpecificFeature2() is IComponent2 c && !seedNames.Contains(c.Name2)) facts.Instances.Add(c);
        }
        return facts;
    }

    /// <summary>The seeds a definition gives (a getter: reading a SAFEARRAY of dispatch is safe, setting one is not).</summary>
    static List<IComponent2> Seeds(object definition)
    {
        object? array = definition switch
        {
            ILocalLinearPatternFeatureData l => l.SeedComponentArray,
            ILocalCircularPatternFeatureData c => c.SeedComponentArray,
            IDerivedPatternFeatureData d => d.SeedComponentArray,
            _ => null,
        };
        return array is object[] items ? items.OfType<IComponent2>().ToList() : new List<IComponent2>();
    }

    void Access(object definition, bool start)
    {
        switch (definition)
        {
            case ILocalLinearPatternFeatureData l:
                if (start) l.AccessSelections(_doc, null);
                else l.ReleaseSelectionAccess();
                break;
            case ILocalCircularPatternFeatureData c:
                if (start) c.AccessSelections(_doc, null);
                else c.ReleaseSelectionAccess();
                break;
            case IDerivedPatternFeatureData d:
                if (start) d.AccessSelections(_doc, null);
                else d.ReleaseSelectionAccess();
                break;
        }
    }

    public object? LinearPattern(ComponentLinearSpec s, out string tried)
    {
        var attempts = new List<string>();
        foreach (var (label, seeds, direction, direction2) in LinearMarks)
        {
            var made = Attempt(attempts, $"FeatureLinearPattern5, {label}", () =>
            {
                Pick(s.Direction, direction);
                if (s.Direction2 != null) Pick(s.Direction2, direction2);
                foreach (var seed in s.Seeds) Pick(seed, seeds);
                return _doc.FeatureManager.FeatureLinearPattern5(s.Count, s.SpacingMm / Mm, s.Direction2 == null ? 1 : s.Count2, s.Direction2 == null ? 0 : s.SpacingMm2 / Mm,
                    s.Reverse, s.Reverse2, "NULL", "NULL", false, false, false, false, false, false, false, false, false, false, 0, 0, false, false);
            }, ComponentPatternKind.Linear);
            if (made != null)
            {
                tried = string.Join("; ", attempts);
                return made;
            }
        }
        tried = string.Join("; ", attempts);
        return null;
    }

    public object? CircularPattern(ComponentCircularSpec s, out string tried)
    {
        var attempts = new List<string>();
        foreach (var (label, seeds, axis) in CircularMarks)
        {
            var made = Attempt(attempts, $"FeatureCircularPattern5, {label}", () =>
            {
                Pick(s.Axis, axis);
                foreach (var seed in s.Seeds) Pick(seed, seeds);
                return _doc.FeatureManager.FeatureCircularPattern5(s.Count, s.AngleDeg * Math.PI / 180, s.Reverse, "NULL", false, s.EqualSpacing, false, false,
                    false, false, 1, 0, "NULL", false);
            }, ComponentPatternKind.Circular);
            if (made != null)
            {
                tried = string.Join("; ", attempts);
                return made;
            }
        }
        tried = string.Join("; ", attempts);
        return null;
    }

    public object? FeaturePattern(IReadOnlyList<object> seeds, object drivingFeature, out string tried)
    {
        var attempts = new List<string>();
        var made = Attempt(attempts, "InsertDerivedPattern2, seeds mark 1, driving feature mark 2", () =>
        {
            foreach (var seed in seeds) Pick(seed, 1);
            Pick(drivingFeature, 2);
            return _doc.FeatureManager.InsertDerivedPattern2();
        }, ComponentPatternKind.Feature);
        made ??= Attempt(attempts, "IAssemblyDoc.InsertDerivedPattern, seeds mark 1, driving feature mark 6", () =>
        {
            foreach (var seed in seeds) Pick(seed, 1);
            Pick(drivingFeature, 6);
            int before = Patterns().Count;
            if (!_asm.InsertDerivedPattern()) return null;
            var all = Patterns();
            return all.Count > before ? all[all.Count - 1] : null;
        }, ComponentPatternKind.Feature);
        tried = string.Join("; ", attempts);
        return made;
    }

    /// <summary>
    /// One way of asking: the selection made afresh, the call, the selection cleared. Kept only when SOLIDWORKS made a
    /// component pattern of the kind asked for; anything else it made is deleted again, and the attempt says so.
    /// </summary>
    object? Attempt(List<string> attempts, string label, Func<object?> call, ComponentPatternKind kind)
    {
        _doc.ClearSelection2(true);
        object? made;
        try
        {
            made = call();
        }
        catch (Exception e) when (e is COMException || e is SwAgentException)
        {
            attempts.Add($"{label}: {e.Message}");
            return null;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
        if (!(made is IFeature f))
        {
            attempts.Add($"{label}: returned null");
            return null;
        }
        if (KindOf(f) == kind)
        {
            attempts.Add($"{label}: made {f.Name}");
            return f;
        }
        attempts.Add($"{label}: made {f.Name}, a {f.GetTypeName2()}, not a {WireNamesOf(kind)} component pattern; deleted it");
        DeleteFeatures(new[] { f });
        return null;
    }

    static string WireNamesOf(ComponentPatternKind kind) => WireNames.ForEnum(kind);

    void Pick(object item, int mark)
    {
        var data = ((ISelectionMgr)_doc.SelectionManager).CreateSelectData();
        data.Mark = mark;
        bool selected = item switch
        {
            IComponent2 c => c.Select4(true, data, false),
            IFeature f => f.Select2(true, mark),
            IEntity e => e.Select4(true, data),
            _ => false,
        };
        if (!selected) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS would not select a {item.GetType().Name} with mark {mark}");
    }

    public void DeletePatterns(IReadOnlyList<object> patterns) => DeleteFeatures(patterns.Cast<IFeature>().ToList());

    void DeleteFeatures(IReadOnlyList<IFeature> features)
    {
        _doc.ClearSelection2(true);
        try
        {
            foreach (var f in features)
            {
                if (!f.Select2(true, 0)) throw new SwAgentException(ErrorCode.ApiError, $"{f.Name} could not be selected to delete it");
            }
            if (!_doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", features.Select(f => f.Name))}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    /// <summary>The feature's faces in the component's own document (its same-named feature), moved into the assembly by the component's placement.</summary>
    public IReadOnlyList<EntityInfo> FeatureFaces(object component, object feature)
    {
        var list = new List<EntityInfo>();
        var c = (IComponent2)component;
        if (!(ModelOf(c) is ISwDocument model) || !(feature is IFeature f) || !(model.FeatureByName(f.Name) is object own)) return list;
        var placement = TransformOf(c);
        foreach (var face in model.FacesOf(own).Concat(PatternedFeatures(model, own).SelectMany(model.FacesOf)))
        {
            var info = model.Describe(face);
            Placement.MoveToAssembly(placement, info);
            info.Component = c.Name2;
            list.Add(info);
        }
        return list;
    }

    /// <summary>
    /// The features a part's linear or circular pattern repeats (PatternFeatureArray), so that the seed's own hole counts
    /// alongside the pattern's copies; none for any other feature.
    /// </summary>
    /// <remarks>
    /// Measured on SOLIDWORKS 2026 SP4 (plan 4.D live run): a CirPattern's GetFaces gives only the copies, not the faces of
    /// the cut it repeats, so a pin on the seed hole was 28.284 mm from the nearest of the three it listed.
    /// </remarks>
    static List<object> PatternedFeatures(ISwDocument model, object feature)
    {
        object definition = ((IFeature)feature).GetDefinition();
        List<object> Read() => (definition switch
        {
            ILinearPatternFeatureData l => l.PatternFeatureArray,
            ICircularPatternFeatureData c => c.PatternFeatureArray,
            _ => null,
        }) is object[] items ? items.OfType<IFeature>().Cast<object>().ToList() : new List<object>();
        var seeds = Read();
        if (seeds.Count > 0 || !(definition is ILinearPatternFeatureData || definition is ICircularPatternFeatureData)) return seeds;
        var doc = (ModelDoc2)model.ApiObject();
        bool accessed = definition is ILinearPatternFeatureData la ? la.AccessSelections(doc, null) : ((ICircularPatternFeatureData)definition).AccessSelections(doc, null);
        try
        {
            return Read();
        }
        finally
        {
            if (accessed)
            {
                if (definition is ILinearPatternFeatureData lr) lr.ReleaseSelectionAccess();
                else ((ICircularPatternFeatureData)definition).ReleaseSelectionAccess();
            }
        }
    }

    public IInterferenceSession OpenInterference(InterferenceOptions options) => new SolidWorksInterference(_asm, options);
}
