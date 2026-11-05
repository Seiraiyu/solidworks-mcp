using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.swconst;

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
