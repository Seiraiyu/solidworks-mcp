using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>Phase 3.D's features of a <see cref="FakeDocument"/>, each changing the part's volume as SOLIDWORKS would.</summary>
sealed partial class FakeDocument
{
    public List<(object Profile, SweepSpec Spec)> Sweeps { get; } = new List<(object, SweepSpec)>();

    /// <summary>The profile's area times the path's length (a path sketch's segments, or an edge's length); 100 mm² for a profile it cannot measure.</summary>
    public object? Sweep(object profile, SweepSpec spec)
    {
        Sweeps.Add((profile, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the profile a feature needs");
        if (FeatureRefuses) return null;
        var contents = ReadSketch(profile);
        double area = FeatureFacts.Region(contents)?.AreaMm2 ?? 100;
        var path = (FakeEntity)spec.Path;
        double length = path.Kind == ReferenceKind.Edge
            ? path.Info.LengthMm ?? 10
            : ReadSketch(path).Segments.Where(x => !x.Construction).Sum(x => x.LengthMm > 0 ? x.LengthMm : Length(x));
        return MakeFeature(spec.Cut ? "Cut-Sweep" : "Sweep", spec.Cut ? "SweepCut" : "Sweep", (spec.Cut ? -1 : 1) * area * length, contents.Segments.Count + 2);
    }

    static double Length(SketchSegmentRecord s) =>
        s.Start == null || s.End == null ? 0 : Math.Sqrt(Math.Pow(s.End.X - s.Start.X, 2) + Math.Pow(s.End.Y - s.Start.Y, 2));

    public List<LoftSpec> Lofts { get; } = new List<LoftSpec>();

    /// <summary>The profiles' mean area times ThroughMm (how far apart the fake takes its profiles to be).</summary>
    public object? Loft(LoftSpec spec)
    {
        Lofts.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the profiles a feature needs");
        if (FeatureRefuses) return null;
        double area = spec.Profiles.Average(p => FeatureFacts.Region(ReadSketch(p))?.AreaMm2 ?? 100);
        return MakeFeature(spec.Cut ? "Cut-Loft" : "Loft", spec.Cut ? "BlendCut" : "Blend", (spec.Cut ? -1 : 1) * area * ThroughMm, 2 + spec.Profiles.Count * 4);
    }

    public List<RibSpec> Ribs { get; } = new List<RibSpec>();

    /// <summary>The sketch's drawn length times the thickness times ThroughMm (how far the fake's rib runs to the body).</summary>
    public object? Rib(object sketch, RibSpec spec)
    {
        Ribs.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch a feature needs");
        if (FeatureRefuses) return null;
        double length = ReadSketch(sketch).Segments.Where(x => !x.Construction).Sum(x => x.LengthMm > 0 ? x.LengthMm : Length(x));
        return MakeFeature("Rib", "Rib", length * spec.ThicknessMm * ThroughMm, 4);
    }

    public List<(IReadOnlyList<object> Faces, double ThicknessMm, bool Outward)> Shells { get; } = new List<(IReadOnlyList<object>, double, bool)>();

    /// <summary>Faces handed to a shell stay in the part (SOLIDWORKS not opening them).</summary>
    public bool FacesSurvive { get; set; }

    /// <summary>The faces removed are deleted; inward takes 1000 mm³ per mm of thickness, outward adds as much.</summary>
    public object? Shell(IReadOnlyList<object> facesToRemove, double thicknessMm, bool outward)
    {
        Shells.Add((facesToRemove, thicknessMm, outward));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the face a feature needs");
        if (FeatureRefuses) return null;
        if (!FacesSurvive) foreach (var f in facesToRemove.Cast<FakeEntity>()) f.State = PersistState.Deleted;
        return MakeFeature("Shell", "Shell", (outward ? 1 : -1) * 1000 * thicknessMm, 6);
    }
}
