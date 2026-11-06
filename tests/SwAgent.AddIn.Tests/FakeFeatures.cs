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

    public List<RefSpec> Axes { get; } = new List<RefSpec>();
    public List<RefSpec> Points { get; } = new List<RefSpec>();

    /// <summary>An axis on the line its references give, RefGeometryError off it.</summary>
    public object? Axis(RefSpec spec)
    {
        Axes.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the entity an axis needs");
        if (FeatureRefuses) return null;
        var r = spec.References.Cast<FakeEntity>().Select(e => e.Info).ToList();
        Vector3 dir;
        Point3 at;
        switch (spec.AxisMode)
        {
            case AxisMode.TwoPlanes:
                var (n1, n2) = (r[0].Normal!, r[1].Normal!);
                var u = Cross(n1, n2);
                double uu = u.X * u.X + u.Y * u.Y + u.Z * u.Z;
                double d1 = Dot(n1, r[0].Origin), d2 = Dot(n2, r[1].Origin);
                var a = Cross(n2, u);
                var b = Cross(u, n1);
                dir = Unit(u);
                at = new Point3((d1 * a.X + d2 * b.X) / uu, (d1 * a.Y + d2 * b.Y) / uu, (d1 * a.Z + d2 * b.Z) / uu);
                break;
            case AxisMode.TwoPoints:
                var (p, q) = (r[0].Origin!, r[1].Origin!);
                dir = Unit(new Vector3(q.X - p.X, q.Y - p.Y, q.Z - p.Z));
                at = p;
                break;
            default:
                dir = r[0].Axis!;
                at = r[0].Origin!;
                break;
        }
        var off = Unit(Math.Abs(dir.X) < 0.9 ? Cross(dir, new Vector3(1, 0, 0)) : Cross(dir, new Vector3(0, 1, 0)));
        var axis = MakeFeature("Axis", "RefAxis", 0, 0, ReferenceKind.Axis);
        axis.Info.Geometry = GeometryType.Line;
        axis.Info.Axis = dir;
        axis.Info.Origin = Along(at, off, RefGeometryError);
        return axis;
    }

    /// <summary>A point at its first reference's origin (the circle's or face's centre, where the edges meet), RefGeometryError off it in x.</summary>
    public object? Point(RefSpec spec)
    {
        Points.Add(spec);
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the entity a point needs");
        if (FeatureRefuses) return null;
        var o = ((FakeEntity)spec.References[0]).Info.Origin ?? new Point3(0, 0, 0);
        var point = MakeFeature("Point", "RefPoint", 0, 0, ReferenceKind.Point);
        point.Info.Geometry = GeometryType.Point;
        point.Info.Origin = new Point3(o.X + RefGeometryError, o.Y, o.Z);
        return point;
    }

    static double Dot(Vector3 n, Point3? p) => p == null ? 0 : n.X * p.X + n.Y * p.Y + n.Z * p.Z;

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

    /// <summary>Suppress returns false (SOLIDWORKS refusing).</summary>
    public bool SuppressRefused { get; set; }

    /// <summary>Suppress returns true and changes nothing.</summary>
    public bool SuppressIgnored { get; set; }

    public List<(string Name, bool Suppressed)> Suppressions { get; } = new List<(string, bool)>();

    /// <summary>Marks the feature suppressed or not in the tree, and takes its volume out of the part or puts it back.</summary>
    public bool Suppress(object feature, bool suppressed)
    {
        var f = (FakeEntity)feature;
        Suppressions.Add((f.Info.Name ?? "?", suppressed));
        if (SuppressRefused) return false;
        if (SuppressIgnored) return true;
        int i = Tree.FindIndex(t => ReferenceEquals(t.Feature, f));
        var old = Tree[i];
        if (old.Suppressed == suppressed) return true;
        Tree[i] = new FeatureRecord(f, old.Name, old.Type, old.Parent, suppressed, old.ErrorCode, old.Warning);
        if (_volumeOf.TryGetValue(f, out double v)) MassProperties!.VolumeMm3 += suppressed ? -v : v;
        return true;
    }

    /// <summary>ClosestDistance throws api_error (SOLIDWORKS unable to measure).</summary>
    public bool MeasureFails { get; set; }

    /// <summary>Each ClosestDistance call reads this much more than the last (the part changing while it is measured).</summary>
    public double MeasureDrift { get; set; }

    int _measures;

    /// <summary>The distance between the two entities' origins, which are the closest points.</summary>
    public double ClosestDistance(object a, object b, out Point3 onA, out Point3 onB)
    {
        if (MeasureFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS could not measure between them");
        onA = ((FakeEntity)a).Info.Origin ?? new Point3(0, 0, 0);
        onB = ((FakeEntity)b).Info.Origin ?? new Point3(0, 0, 0);
        return Math.Sqrt(Math.Pow(onA.X - onB.X, 2) + Math.Pow(onA.Y - onB.Y, 2) + Math.Pow(onA.Z - onB.Z, 2)) + MeasureDrift * _measures++;
    }

    /// <summary>OpenForEditing returns this (SOLIDWORKS opening the sketch or not).</summary>
    public bool OpensForEditing { get; set; } = true;

    public bool OpenForEditing(object sketch)
    {
        if (!OpensForEditing) return false;
        EditingSketchName = ((FakeEntity)sketch).Info.Name;
        return true;
    }

    /// <summary>ExitSketch leaves the sketch open.</summary>
    public bool ExitIgnored { get; set; }

    public void ExitSketch()
    {
        if (!ExitIgnored) EditingSketchName = null;
    }

    public IReadOnlyList<object> FacesOf(object feature) =>
        _facesOf.TryGetValue((FakeEntity)feature, out var faces) ? faces.Where(Entities.Contains).Cast<object>().ToList() : new List<object>();

    /// <summary>The features built on each feature, by name (what ChildrenOf reports).</summary>
    public Dictionary<string, List<string>> Children { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public IReadOnlyList<string> ChildrenOf(object feature) =>
        Children.TryGetValue(((FakeEntity)feature).Info.Name ?? "", out var names) ? names.ToList() : new List<string>();

    /// <summary>Delete fails as SolidWorksDocument does when SOLIDWORKS does not select or delete (api_error).</summary>
    public bool DeleteThrowsApiError { get; set; }

    /// <summary>Delete reports success and deletes nothing.</summary>
    public bool DeleteIgnored { get; set; }

    /// <summary>Features a delete with children leaves in the part (SOLIDWORKS not deleting a grandchild).</summary>
    public HashSet<string> DeleteSpares { get; } = new HashSet<string>(StringComparer.Ordinal);

    public void Delete(object feature, bool children, bool absorbed)
    {
        var f = (FakeEntity)feature;
        Deletes.Add((f.Info.Name ?? "?", children, absorbed));
        if (DeleteFails) throw new InvalidOperationException("SOLIDWORKS did not delete it");
        if (DeleteThrowsApiError) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select {f.Info.Name} to delete it");
        if (DeleteIgnored) return;
        if (children)
        {
            foreach (var child in ChildrenOf(f).Where(n => !DeleteSpares.Contains(n)).Select(FeatureByName).OfType<FakeEntity>().ToList()) Delete(child, true, absorbed);
        }
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
