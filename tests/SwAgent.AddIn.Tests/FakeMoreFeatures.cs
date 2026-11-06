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
}
