using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Features of a <see cref="FakeDocument"/>: each one made is a feature in the tree with faces of its own, and changes
/// the part's volume as SOLIDWORKS would (an extrude by its profile's area times its depth), so the operations' exact
/// checks can be proven; deleting it takes its faces and its volume back.
/// </summary>
sealed partial class FakeDocument
{
    readonly Dictionary<FakeEntity, List<FakeEntity>> _facesOf = new Dictionary<FakeEntity, List<FakeEntity>>();
    readonly Dictionary<FakeEntity, double> _volumeOf = new Dictionary<FakeEntity, double>();
    readonly HashSet<FakeEntity> _madeBody = new HashSet<FakeEntity>();
    readonly Dictionary<string, int> _named = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Feature calls make nothing (SOLIDWORKS returning no feature).</summary>
    public bool FeatureRefuses { get; set; }

    /// <summary>Feature calls fail to select what they need (api_error).</summary>
    public bool FeatureSelectFails { get; set; }

    /// <summary>Added to every volume change a feature makes (a feature that is not what was asked).</summary>
    public double FeatureVolumeError { get; set; }

    /// <summary>Features change no volume.</summary>
    public bool FeatureAddsNothing { get; set; }

    /// <summary>How far a through_all, up_to_next or up_to end reaches, mm.</summary>
    public double ThroughMm { get; set; } = 10;

    /// <summary>Features are made with this SOLIDWORKS type name instead of their own.</summary>
    public string? FeatureTypeOverride { get; set; }

    public List<ExtrudeSpec> Extrudes { get; } = new List<ExtrudeSpec>();

    /// <summary>Every Delete call: the feature, children, absorbed.</summary>
    public List<(string Name, bool Children, bool Absorbed)> Deletes { get; } = new List<(string, bool, bool)>();

    public object? Extrude(object sketch, ExtrudeSpec spec)
    {
        Extrudes.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch a feature needs");
        if (FeatureRefuses) return null;
        var contents = ReadSketch(sketch);
        double area = spec.Thin != null ? spec.Thin.ThicknessMm * 40 : FeatureFacts.Region(contents)?.AreaMm2 ?? 100;
        double depth = spec.End == ExtrudeEnd.Blind || spec.End == ExtrudeEnd.MidPlane ? spec.DepthMm : ThroughMm;
        if (spec.Direction2 != null) depth += spec.Direction2.End == ExtrudeEnd.Blind ? spec.Direction2.DepthMm : ThroughMm;
        return MakeFeature(spec.Cut ? "Cut-Extrude" : "Boss-Extrude", spec.Cut ? "Cut" : "Extrusion", (spec.Cut ? -1 : 1) * area * depth, contents.Segments.Count + 2);
    }

    public List<RevolveSpec> Revolves { get; } = new List<RevolveSpec>();

    /// <summary>Pappus' volume when the axis is a line of the sketch, else 1000 mm³; negative for a cut.</summary>
    public object? Revolve(object sketch, RevolveSpec spec)
    {
        Revolves.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the axis a feature needs");
        if (FeatureRefuses) return null;
        var contents = ReadSketch(sketch);
        var region = FeatureFacts.Region(contents);
        var line = contents.Segments.FirstOrDefault(s => s.Entity == spec.Axis);
        double volume = region != null && line != null ? SwAgent.AddIn.Core.FeatureChecks.PappusVolume(region, line.Start!, line.End!, spec.AngleDeg) : 1000;
        return MakeFeature(spec.Cut ? "Cut-Revolve" : "Revolve", spec.Cut ? "RevCut" : "Revolution", (spec.Cut ? -1 : 1) * volume, contents.Segments.Count(s => !s.Construction));
    }

    /// <summary>Fillets and chamfers leave the edges they were given in the part (SOLIDWORKS not rounding them).</summary>
    public bool EdgesSurvive { get; set; }

    /// <summary>The volume a fillet or chamfer removes per edge, mm³.</summary>
    public double EdgeVolume { get; set; } = 8.584;

    public List<(IReadOnlyList<object> Targets, double RadiusMm)> Fillets { get; } = new List<(IReadOnlyList<object>, double)>();

    public object? Fillet(IReadOnlyList<object> edgesAndFaces, double radiusMm)
    {
        Fillets.Add((edgesAndFaces, radiusMm));
        return EdgeFeature("Fillet", edgesAndFaces);
    }

    public List<(IReadOnlyList<object> Edges, ChamferSpec Spec)> Chamfers { get; } = new List<(IReadOnlyList<object>, ChamferSpec)>();

    public object? Chamfer(IReadOnlyList<object> edges, ChamferSpec spec)
    {
        Chamfers.Add((edges, spec));
        return EdgeFeature("Chamfer", edges);
    }

    /// <summary>A fillet or chamfer: one face per edge or face it was given, its edges consumed, EdgeVolume removed per edge.</summary>
    FakeEntity? EdgeFeature(string type, IReadOnlyList<object> targets)
    {
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the edge a feature needs");
        if (FeatureRefuses) return null;
        var edges = targets.Cast<FakeEntity>().Where(e => e.Kind == ReferenceKind.Edge).ToList();
        if (!EdgesSurvive) edges.ForEach(e => e.State = PersistState.Deleted);
        return MakeFeature(type, type, -EdgeVolume * Math.Max(1, edges.Count), targets.Count);
    }

    public List<PlaneSpec> Planes { get; } = new List<PlaneSpec>();

    /// <summary>Reference geometry is made this far, mm, along its normal from where it should be.</summary>
    public double RefGeometryError { get; set; }

    /// <summary>A plane where the spec puts it, worked out from its references' descriptions.</summary>
    public object? Plane(PlaneSpec spec)
    {
        Planes.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the plane a feature needs");
        if (FeatureRefuses) return null;
        var r = spec.References.Cast<FakeEntity>().Select(e => e.Info).ToList();
        Vector3 n;
        Point3 o;
        switch (spec.Mode)
        {
            case PlaneMode.Offset:
                n = r[0].Normal!;
                o = Along(r[0].Origin ?? new Point3(0, 0, 0), n, (spec.Flip ? -1 : 1) * spec.DistanceMm);
                break;
            case PlaneMode.Angle:
                n = Rotate(r[0].Normal!, r[1].Axis!, spec.AngleDeg);
                o = r[1].Origin!;
                break;
            case PlaneMode.ThreePoints:
                var (a, b, p) = (r[0].Origin!, r[1].Origin!, r[2].Origin!);
                n = Unit(Cross(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Vector3(p.X - a.X, p.Y - a.Y, p.Z - a.Z)));
                o = a;
                break;
            case PlaneMode.ParallelThroughPoint:
                n = r[0].Normal!;
                o = r[1].Origin!;
                break;
            default:
                n = r[0].Axis ?? new Vector3(0, 0, 1);
                o = r[1].Origin!;
                break;
        }
        var plane = MakeFeature("Plane", "RefPlane", 0, 0, ReferenceKind.Plane);
        plane.Info.Geometry = GeometryType.Plane;
        plane.Info.Normal = n;
        plane.Info.Origin = Along(o, n, RefGeometryError);
        return plane;
    }

    static Point3 Along(Point3 o, Vector3 n, double d) => new Point3(o.X + n.X * d, o.Y + n.Y * d, o.Z + n.Z * d);

    static Vector3 Cross(Vector3 u, Vector3 v) => new Vector3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);

    static Vector3 Unit(Vector3 v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return new Vector3(v.X / l, v.Y / l, v.Z / l);
    }

    /// <summary>Rodrigues: <paramref name="v"/> turned <paramref name="deg"/> degrees about the unit axis <paramref name="k"/>.</summary>
    static Vector3 Rotate(Vector3 v, Vector3 k, double deg)
    {
        double t = deg * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
        var kxv = Cross(k, v);
        double kv = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
        return new Vector3(v.X * cos + kxv.X * sin + k.X * kv * (1 - cos), v.Y * cos + kxv.Y * sin + k.Y * kv * (1 - cos), v.Z * cos + kxv.Z * sin + k.Z * kv * (1 - cos));
    }

    /// <summary>A new feature named as SOLIDWORKS names them (Boss-Extrude1, Boss-Extrude2 …), with <paramref name="faces"/> planar faces of its own.</summary>
    FakeEntity MakeFeature(string prefix, string type, double volume, int faces, ReferenceKind kind = ReferenceKind.Feature)
    {
        _named[prefix] = _named.TryGetValue(prefix, out int n) ? n + 1 : 1;
        string name = prefix + _named[prefix];
        var feature = AddFeature(name, FeatureTypeOverride ?? type, kind);
        _facesOf[feature] = Enumerable.Range(0, faces).Select(_ => Add(ReferenceKind.Face, new EntityInfo { Feature = name, Geometry = GeometryType.Plane, AreaMm2 = 1 })).ToList();
        double change = FeatureAddsNothing ? 0 : volume + (volume == 0 ? 0 : FeatureVolumeError);
        _volumeOf[feature] = change;
        MassProperties ??= new ModelMassResult();
        MassProperties.VolumeMm3 += change;
        if (Bodies == 0 && change > 0)
        {
            Bodies = 1;
            _madeBody.Add(feature);
        }
        return feature;
    }

    public IReadOnlyList<object> FacesOf(object feature) =>
        _facesOf.TryGetValue((FakeEntity)feature, out var faces) ? faces.Where(Entities.Contains).Cast<object>().ToList() : new List<object>();

    public void Delete(object feature, bool children, bool absorbed)
    {
        var f = (FakeEntity)feature;
        Deletes.Add((f.Info.Name ?? "?", children, absorbed));
        if (DeleteFails) throw new InvalidOperationException("SOLIDWORKS did not delete it");
        RemoveFeature(f);
        if (_facesOf.TryGetValue(f, out var faces)) Entities.RemoveAll(faces.Contains);
        if (_volumeOf.TryGetValue(f, out double v))
        {
            MassProperties!.VolumeMm3 -= v;
            _volumeOf.Remove(f);
        }
        if (_madeBody.Remove(f)) Bodies = 0;
    }
}
