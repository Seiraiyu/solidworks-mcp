using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>SOLIDWORKS' error and warning bit flags as the names agents see (the roadmap's error model: SOLIDWORKS' own enum names).</summary>
public static class SolidWorksCodes
{
    public static IReadOnlyList<string> SaveErrors(int flags) => Names<swFileSaveError_e>(flags);

    public static IReadOnlyList<string> SaveWarnings(int flags) => Names<swFileSaveWarning_e>(flags);

    public static IReadOnlyList<string> LoadErrors(int flags) => Names<swFileLoadError_e>(flags);

    public static IReadOnlyList<string> LoadWarnings(int flags) => Names<swFileLoadWarning_e>(flags);

    /// <summary>A feature's rebuild error code (IFeature.GetErrorCode2, What's Wrong) as its swFeatureError_e name.</summary>
    public static string FeatureError(int code) => Enum.GetName(typeof(swFeatureError_e), code) ?? $"unknown_{code}";

    /// <summary>The swConstraintType_e a sketch relation of this type has.</summary>
    public static int Constraint(SketchRelationType type) => (int)(type switch
    {
        SketchRelationType.Horizontal => swConstraintType_e.swConstraintType_HORIZONTAL,
        SketchRelationType.Vertical => swConstraintType_e.swConstraintType_VERTICAL,
        SketchRelationType.Coincident => swConstraintType_e.swConstraintType_COINCIDENT,
        SketchRelationType.Tangent => swConstraintType_e.swConstraintType_TANGENT,
        SketchRelationType.Parallel => swConstraintType_e.swConstraintType_PARALLEL,
        SketchRelationType.Perpendicular => swConstraintType_e.swConstraintType_PERPENDICULAR,
        SketchRelationType.Equal => swConstraintType_e.swConstraintType_SAMELENGTH,
        SketchRelationType.Concentric => swConstraintType_e.swConstraintType_CONCENTRIC,
        SketchRelationType.Midpoint => swConstraintType_e.swConstraintType_ATMIDDLE,
        SketchRelationType.Collinear => swConstraintType_e.swConstraintType_COLINEAR,
        SketchRelationType.Symmetric => swConstraintType_e.swConstraintType_SYMMETRIC,
        _ => swConstraintType_e.swConstraintType_FIXED,
    });

    /// <summary>A relation's swConstraintType_e as an agent reads it: the sketch.create name where there is one, else SOLIDWORKS' own in lower case.</summary>
    public static string RelationName(int constraint)
    {
        foreach (SketchRelationType type in Enum.GetValues(typeof(SketchRelationType)))
        {
            if (Constraint(type) == constraint) return WireNames.ForEnum(type);
        }
        if (constraint == (int)swConstraintType_e.swConstraintType_HORIZPOINTS) return "horizontal";
        if (constraint == (int)swConstraintType_e.swConstraintType_VERTPOINTS) return "vertical";
        // Two points at one place are made coincident by merging them (3.B live run).
        if (constraint == (int)swConstraintType_e.swConstraintType_MERGEPOINTS) return "coincident";
        string? name = Enum.GetName(typeof(swConstraintType_e), constraint);
        return name == null ? $"unknown_{constraint}" : name.Substring("swConstraintType_".Length).ToLowerInvariant();
    }

    /// <summary>A selected object's swSelectType_e as the name selection results use.</summary>
    public static string SelectType(int type) => type switch
    {
        (int)swSelectType_e.swSelEDGES => "edge",
        (int)swSelectType_e.swSelFACES => "face",
        (int)swSelectType_e.swSelVERTICES => "vertex",
        (int)swSelectType_e.swSelDATUMPLANES => "plane",
        (int)swSelectType_e.swSelDATUMAXES => "axis",
        (int)swSelectType_e.swSelDATUMPOINTS => "point",
        (int)swSelectType_e.swSelSKETCHES => "sketch",
        (int)swSelectType_e.swSelSKETCHSEGS => "sketch_segment",
        (int)swSelectType_e.swSelSKETCHPOINTS => "sketch_point",
        (int)swSelectType_e.swSelDIMENSIONS => "dimension",
        (int)swSelectType_e.swSelBODYFEATURES => "feature",
        (int)swSelectType_e.swSelSOLIDBODIES => "solid_body",
        _ => "other",
    };

    /// <summary>The names of the single-bit members of <typeparamref name="T"/> set in <paramref name="flags"/>, and unknown_&lt;bit&gt; for any other bit.</summary>
    public static IReadOnlyList<string> Names<T>(int flags) where T : struct, Enum
    {
        var names = new List<string>();
        var known = Enum.GetValues(typeof(T)).Cast<T>().Select(v => (Name: v.ToString(), Value: Convert.ToInt32(v))).ToList();
        for (int bit = 0; bit < 31; bit++)
        {
            int mask = 1 << bit;
            if ((flags & mask) == 0) continue;
            var match = known.FirstOrDefault(k => k.Value == mask);
            names.Add(match.Name ?? $"unknown_{mask}");
        }
        return names;
    }
}
