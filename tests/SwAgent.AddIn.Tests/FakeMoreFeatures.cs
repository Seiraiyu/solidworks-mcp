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

    public List<DraftSpec> Drafts { get; } = new List<DraftSpec>();

    /// <summary>Added to the angle every drafted face is tilted by, degrees (a draft that is not what was asked).</summary>
    public double DraftAngleError { get; set; }

    /// <summary>The drafted faces are made anew (their references no longer resolve), as the draft's own faces.</summary>
    public bool DraftRemakesFaces { get; set; }

    /// <summary>
    /// Each face is tilted by the angle (and DraftAngleError) from standing square to the neutral normal: its normal is
    /// turned towards the neutral normal. 10 mm³ per degree per face comes off the part.
    /// </summary>
    public object? Draft(DraftSpec spec)
    {
        Drafts.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the face a feature needs");
        if (FeatureRefuses) return null;
        var n = ((FakeEntity)spec.Neutral).Info.Normal!;
        var draft = MakeFeature("Draft", "Draft", -10 * spec.AngleDeg * spec.Faces.Count, 0);
        double t = (spec.AngleDeg + DraftAngleError) * Math.PI / 180;
        foreach (var face in spec.Faces.Cast<FakeEntity>())
        {
            var f = face.Info.Normal ?? new Vector3(1, 0, 0);
            var tilted = new Vector3(f.X * Math.Cos(t) + n.X * Math.Sin(t), f.Y * Math.Cos(t) + n.Y * Math.Sin(t), f.Z * Math.Cos(t) + n.Z * Math.Sin(t));
            if (DraftRemakesFaces)
            {
                face.State = PersistState.Deleted;
                _facesOf[draft].Add(Add(ReferenceKind.Face, new EntityInfo { Feature = draft.Info.Name, Geometry = GeometryType.Plane, Normal = tilted, AreaMm2 = face.Info.AreaMm2 }));
            }
            else
            {
                var before = face.Info.Normal;
                face.Info.Normal = tilted;
                _facesOf[draft].Add(face);
                Undo(draft, () =>
                {
                    face.Info.Normal = before;
                    _facesOf[draft].Remove(face);
                });
            }
        }
        return draft;
    }

    readonly Dictionary<FakeEntity, List<Action>> _undo = new Dictionary<FakeEntity, List<Action>>();

    /// <summary>Something Delete puts back when it deletes <paramref name="feature"/> (a draft's tilted faces).</summary>
    void Undo(FakeEntity feature, Action undo)
    {
        if (!_undo.TryGetValue(feature, out var list)) _undo[feature] = list = new List<Action>();
        list.Add(undo);
    }

    public List<HoleSpec> Holes { get; } = new List<HoleSpec>();

    /// <summary>The fake's holes are made this far, mm, along x from their positions (a hole in the wrong place).</summary>
    public double HoleOffsetMm { get; set; }

    /// <summary>The fake's hole makes no cylinder for its last position (SOLIDWORKS drilling fewer holes than asked).</summary>
    public bool HoleSkipsOne { get; set; }

    /// <summary>A 2.5 mm radius cylinder per position, on the face's normal (its axis), each taking π r² × depth (or ThroughMm).</summary>
    public object? Hole(HoleSpec spec)
    {
        Holes.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch point a hole goes at");
        if (FeatureRefuses) return null;
        const double r = 2.5;
        double depth = spec.End == HoleEnd.Blind ? spec.DepthMm : ThroughMm;
        int made = spec.Positions.Count - (HoleSkipsOne ? 1 : 0);
        var hole = MakeFeature("Hole", "HoleWzd", -Math.PI * r * r * depth * made, 0);
        var axis = ((FakeEntity)spec.Face).Info.Normal ?? new Vector3(0, 0, 1);
        foreach (var p in spec.Positions.Take(made))
        {
            _facesOf[hole].Add(Add(ReferenceKind.Face, new EntityInfo
            {
                Feature = hole.Info.Name,
                Geometry = GeometryType.Cylinder,
                Axis = axis,
                Origin = new Point3(p.X + HoleOffsetMm, p.Y, p.Z),
                RadiusMm = r,
            }));
            _facesOf[hole].Add(Add(ReferenceKind.Face, new EntityInfo { Feature = hole.Info.Name, Geometry = GeometryType.Plane, Normal = axis }));
        }
        return hole;
    }

    public List<LinearPatternSpec> LinearPatterns { get; } = new List<LinearPatternSpec>();

    /// <summary>The instances each pattern was made with, by its name.</summary>
    readonly Dictionary<string, int> _instances = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Added to what PatternInstances reports (a pattern that is not what was asked).</summary>
    public int InstanceError { get; set; }

    /// <summary>Each new instance adds the seeds' own volume again, and two faces per seed.</summary>
    public object? LinearPattern(LinearPatternSpec spec)
    {
        LinearPatterns.Add(spec);
        return Pattern("LPattern", "LPattern", spec.Seeds, spec.Count * spec.Count2, spec.Faces);
    }

    /// <summary>The volume each patterned face takes per instance, mm³ (the fake's faces carry no volume of their own).</summary>
    public double FaceInstanceVolume { get; set; } = 50;

    FakeEntity? Pattern(string prefix, string type, IReadOnlyList<object> seeds, int instances, IReadOnlyList<object>? faces = null)
    {
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the seed a feature needs");
        if (FeatureRefuses) return null;
        double seed = seeds.Cast<FakeEntity>().Sum(s => _volumeOf.TryGetValue(s, out double v) ? v : 0) - FaceInstanceVolume * (faces?.Count ?? 0);
        var made = MakeFeature(prefix, type, seed * (instances - 1), 2 * (seeds.Count + (faces?.Count ?? 0)) * (instances - 1));
        _instances[made.Info.Name!] = instances;
        return made;
    }

    public int PatternInstances(object feature) =>
        (_instances.TryGetValue(((FakeEntity)feature).Info.Name ?? "", out int n) ? n : 0) + InstanceError;

    public List<CircularPatternSpec> CircularPatterns { get; } = new List<CircularPatternSpec>();

    public object? CircularPattern(CircularPatternSpec spec)
    {
        CircularPatterns.Add(spec);
        return Pattern("CirPattern", "CirPattern", spec.Seeds, spec.Count, spec.Faces);
    }

    public List<MirrorSpec> Mirrors { get; } = new List<MirrorSpec>();

    /// <summary>Features: the seeds' volume once more. A body: the volume doubles (the plane is one of its faces).</summary>
    public object? Mirror(MirrorSpec spec)
    {
        Mirrors.Add(spec);
        if (!spec.Body) return Pattern("Mirror", "MirrorPattern", spec.Seeds, 2);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the body to mirror");
        if (FeatureRefuses) return null;
        return MakeFeature("Mirror", "MirrorSolid", MassProperties?.VolumeMm3 ?? 0, 6);
    }
}
