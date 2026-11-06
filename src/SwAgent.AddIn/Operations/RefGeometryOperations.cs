using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class RefgeomPlaneOperation : ModelChangeOperation<RefgeomPlaneRequest, FeatureResult>
{
    public RefgeomPlaneOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(RefgeomPlaneRequest request) => request.Document;

    /// <summary>The fields each mode takes; any other field given is invalid_argument.</summary>
    static readonly Dictionary<PlaneMode, string[]> Takes = new Dictionary<PlaneMode, string[]>
    {
        [PlaneMode.Offset] = new[] { "from", "distance_mm", "flip" },
        [PlaneMode.Angle] = new[] { "from", "axis", "angle_deg", "flip" },
        [PlaneMode.ThreePoints] = new[] { "points" },
        [PlaneMode.ParallelThroughPoint] = new[] { "from", "through" },
        [PlaneMode.NormalToCurve] = new[] { "curve", "through" },
    };

    protected override FeatureResult Change(ISwDocument doc, RefgeomPlaneRequest request, ModelTransaction tx, IOperationContext context)
    {
        string mode = WireNames.ForEnum(request.Mode);
        var given = new (string Field, bool Given)[]
        {
            ("from", request.From != null), ("distance_mm", request.DistanceMm != null), ("flip", request.Flip != null), ("axis", request.Axis != null),
            ("angle_deg", request.AngleDeg != null), ("points", request.Points != null), ("through", request.Through != null), ("curve", request.Curve != null),
        };
        foreach (var (field, isGiven) in given)
        {
            if (isGiven && !Takes[request.Mode].Contains(field))
                throw FeatureFacts.Invalid(field, $"a {mode} plane does not take {field}; it takes {string.Join(", ", Takes[request.Mode])}");
        }
        var spec = new PlaneSpec { Mode = request.Mode, Flip = request.Flip == true };
        var refs = Resolved(doc, request);
        spec.References.AddRange(refs);
        if (request.Mode == PlaneMode.Offset) spec.DistanceMm = FeatureFacts.Length(request.DistanceMm, "distance_mm", "an offset plane");
        if (request.Mode == PlaneMode.Angle)
        {
            if (!(request.AngleDeg > 0 && request.AngleDeg < 180)) throw FeatureFacts.Invalid("angle_deg", "an angle plane needs angle_deg, more than 0 and less than 180 degrees");
            spec.AngleDeg = request.AngleDeg!.Value;
        }
        var feature = doc.Plane(spec) ?? throw FeatureFacts.NotMade(doc, "reference plane", $"({mode})", "check that its references define one plane");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The request's references, resolved and checked, in SOLIDWORKS' order for the mode.</summary>
    List<object> Resolved(ISwDocument doc, RefgeomPlaneRequest r)
    {
        var references = Services.References;
        object Point(string? text, string field) =>
            references.Resolve(doc, Need(text, field, "a vertex or point"), field, ReferenceKind.Vertex, ReferenceKind.Point).Entity;
        string Need(string? text, string field, string what) => text ?? throw FeatureFacts.Invalid(field, $"a {WireNames.ForEnum(r.Mode)} plane needs {field}: {what}");
        switch (r.Mode)
        {
            case PlaneMode.Offset:
                return new List<object> { RefGeometry.Plane(references, doc, Need(r.From, "from", "a plane or planar face"), "from") };
            case PlaneMode.Angle:
                var from = RefGeometry.Plane(references, doc, Need(r.From, "from", "a plane or planar face"), "from");
                return new List<object> { from, RefGeometry.Line(references, doc, Need(r.Axis, "axis", "a straight edge or an axis"), "axis") };
            case PlaneMode.ThreePoints:
                if (r.Points == null || r.Points.Count != 3) throw FeatureFacts.Invalid("points", "a three_points plane needs exactly three vertices or points");
                return r.Points.Select((p, i) => Point(p, $"/points/{i}")).ToList();
            case PlaneMode.ParallelThroughPoint:
                return new List<object> { RefGeometry.Plane(references, doc, Need(r.From, "from", "a plane or planar face"), "from"), Point(r.Through, "through") };
            default:
                var curve = references.Resolve(doc, Need(r.Curve, "curve", "an edge"), "curve", ReferenceKind.Edge).Entity;
                return new List<object> { curve, Point(r.Through, "through") };
        }
    }

    /// <summary>The feature is a plane, the volume is unchanged, and the plane is where the request put it.</summary>
    protected override void Check(ISwDocument doc, RefgeomPlaneRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "RefPlane", "reference plane");
        FeatureFacts.VolumeChanged(result, "reference plane", VolumeBefore, Volume(doc, doc.SolidBodyCount()), 0);
        var plane = doc.Describe(doc.FeatureByName(result.Feature.Name ?? "")
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} is not in the part", new JsonObject { ["check"] = "feature_exists" }));
        var n = plane.Normal ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} has no normal", new JsonObject { ["check"] = "plane_normal" });
        var o = plane.Origin ?? new Point3(0, 0, 0);
        var refs = Resolved(doc, request).Select(doc.Describe).ToList();
        switch (request.Mode)
        {
            case PlaneMode.Offset:
                RefGeometry.Parallel(refs[0].Normal!, n, result);
                double d = FeatureChecks.Offset(refs[0].Origin ?? new Point3(0, 0, 0), refs[0].Normal!, o);
                FeatureChecks.Near("plane_offset", $"{result.Feature.Name}'s offset from {request.From}", request.DistanceMm!.Value, d, RefGeometry.LengthTolerance, "mm");
                result.Checks.Add(FormattableString.Invariant($"parallel to {request.From}, {d:0.######} mm from it"));
                break;
            case PlaneMode.Angle:
                double want = request.AngleDeg!.Value <= 90 ? request.AngleDeg.Value : 180 - request.AngleDeg.Value;
                double angle = FeatureChecks.AngleDeg(refs[0].Normal!, n);
                FeatureChecks.Near("plane_angle", $"{result.Feature.Name}'s angle to {request.From}", want, angle, RefGeometry.AngleTolerance, "deg");
                RefGeometry.Contains(o, n, refs[1].Origin!, "axis", result);
                if (refs[1].Axis != null)
                    FeatureChecks.Near("plane_contains", $"the angle between {result.Feature.Name}'s normal and the axis", 90, FeatureChecks.AngleDeg(refs[1].Axis!, n), RefGeometry.AngleTolerance, "deg");
                result.Checks.Add(FormattableString.Invariant($"at {angle:0.######}° to {request.From}, through the axis"));
                break;
            case PlaneMode.ThreePoints:
                for (int i = 0; i < 3; i++) RefGeometry.Contains(o, n, refs[i].Origin!, $"/points/{i}", result);
                result.Checks.Add("through the three points");
                break;
            case PlaneMode.ParallelThroughPoint:
                RefGeometry.Parallel(refs[0].Normal!, n, result);
                RefGeometry.Contains(o, n, refs[1].Origin!, "through", result);
                result.Checks.Add($"parallel to {request.From}, through the point");
                break;
            default:
                RefGeometry.Contains(o, n, refs[1].Origin!, "through", result);
                if (refs[0].Geometry == GeometryType.Line && refs[0].Axis != null)
                {
                    FeatureChecks.Near("plane_normal", $"the angle between {result.Feature.Name}'s normal and the curve", 0, FeatureChecks.AngleDeg(refs[0].Axis!, n), RefGeometry.AngleTolerance, "deg");
                    result.Checks.Add("normal to the line, through the point");
                }
                else result.Checks.Add("through the point on the curve");
                break;
        }
    }
}

public sealed class RefgeomAxisOperation : ModelChangeOperation<RefgeomAxisRequest, FeatureResult>
{
    public RefgeomAxisOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(RefgeomAxisRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, RefgeomAxisRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = new RefSpec { AxisMode = request.Mode };
        spec.References.AddRange(Resolved(doc, request));
        var feature = doc.Axis(spec) ?? throw FeatureFacts.NotMade(doc, "reference axis", $"({WireNames.ForEnum(request.Mode)})", "check that its entities define one line");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    List<object> Resolved(ISwDocument doc, RefgeomAxisRequest r)
    {
        var references = Services.References;
        int count = r.Mode == AxisMode.TwoPlanes || r.Mode == AxisMode.TwoPoints ? 2 : 1;
        if (r.Entities.Count != count)
            throw FeatureFacts.Invalid("entities", $"a {WireNames.ForEnum(r.Mode)} axis is built on {count} {(count == 1 ? "entity" : "entities")}, not {r.Entities.Count}");
        return r.Entities.Select((text, i) =>
        {
            string field = $"/entities/{i}";
            switch (r.Mode)
            {
                case AxisMode.TwoPlanes:
                    return RefGeometry.Plane(references, doc, text, field);
                case AxisMode.TwoPoints:
                    return references.Resolve(doc, text, field, ReferenceKind.Vertex, ReferenceKind.Point).Entity;
                case AxisMode.Cylinder:
                    var face = references.Resolve(doc, text, field, ReferenceKind.Face).Entity;
                    var g = doc.Describe(face).Geometry;
                    if (g != GeometryType.Cylinder && g != GeometryType.Cone)
                        throw FeatureFacts.Invalid(field, $"{field}: a cylinder axis needs a cylindrical or conical face, and this is a {WireNames.ForEnum(g)} face");
                    return face;
                default:
                    return RefGeometry.Line(references, doc, text, field);
            }
        }).ToList();
    }

    /// <summary>The feature is an axis, the volume is unchanged, and the axis lies where its entities put it.</summary>
    protected override void Check(ISwDocument doc, RefgeomAxisRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "RefAxis", "reference axis");
        FeatureFacts.VolumeChanged(result, "reference axis", VolumeBefore, Volume(doc, doc.SolidBodyCount()), 0);
        var axis = RefGeometry.Made(doc, result);
        var dir = axis.Axis ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} has no direction", new JsonObject { ["check"] = "axis_direction" });
        var at = axis.Origin ?? new Point3(0, 0, 0);
        var refs = Resolved(doc, request).Select(doc.Describe).ToList();
        string name = result.Feature.Name ?? "the axis";
        switch (request.Mode)
        {
            case AxisMode.TwoPlanes:
                for (int i = 0; i < 2; i++)
                {
                    FeatureChecks.Near("axis_direction", $"the angle between {name} and the normal of /entities/{i}", 90, FeatureChecks.AngleDeg(refs[i].Normal!, dir), RefGeometry.AngleTolerance, "deg");
                    FeatureChecks.Near("axis_position", $"the distance from {name} to /entities/{i}", 0,
                        FeatureChecks.Offset(refs[i].Origin ?? new Point3(0, 0, 0), refs[i].Normal!, at), RefGeometry.LengthTolerance, "mm");
                }
                result.Checks.Add("along the line where the two planes meet");
                break;
            case AxisMode.TwoPoints:
                var (a, b) = (refs[0].Origin!, refs[1].Origin!);
                var ab = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                RefGeometry.Along(ab, a, dir, at, name);
                result.Checks.Add("through the two points");
                break;
            default:
                RefGeometry.Along(refs[0].Axis!, refs[0].Origin!, dir, at, name);
                result.Checks.Add(request.Mode == AxisMode.Cylinder ? "along the face's axis" : "along the edge");
                break;
        }
    }
}

public sealed class RefgeomPointOperation : ModelChangeOperation<RefgeomPointRequest, FeatureResult>
{
    public RefgeomPointOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(RefgeomPointRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, RefgeomPointRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.Mode == PointMode.AlongCurve) FeatureFacts.Length(request.DistanceMm, "distance_mm", "a point along a curve");
        else if (request.DistanceMm != null) throw FeatureFacts.Invalid("distance_mm", "distance_mm is for along_curve only");
        var spec = new RefSpec { PointMode = request.Mode, DistanceMm = request.DistanceMm ?? 0 };
        spec.References.AddRange(Resolved(doc, request));
        var feature = doc.Point(spec) ?? throw FeatureFacts.NotMade(doc, "reference point", $"({WireNames.ForEnum(request.Mode)})", "check that its entities define one point");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    List<object> Resolved(ISwDocument doc, RefgeomPointRequest r)
    {
        int count = r.Mode == PointMode.Intersection ? 2 : 1;
        if (r.Entities.Count != count)
            throw FeatureFacts.Invalid("entities", $"a {WireNames.ForEnum(r.Mode)} point is built on {count} {(count == 1 ? "entity" : "entities")}, not {r.Entities.Count}");
        return r.Entities.Select((text, i) =>
        {
            string field = $"/entities/{i}";
            if (r.Mode == PointMode.FaceCenter) return Services.References.Resolve(doc, text, field, ReferenceKind.Face).Entity;
            var edge = Services.References.Resolve(doc, text, field, ReferenceKind.Edge).Entity;
            var g = doc.Describe(edge).Geometry;
            if (r.Mode == PointMode.ArcCenter && g != GeometryType.Circle)
                throw FeatureFacts.Invalid(field, $"{field}: an arc_center point needs a circular edge, and this is a {WireNames.ForEnum(g)} edge");
            return edge;
        }).ToList();
    }

    /// <summary>The feature is a point, the volume is unchanged, and the point is where its entities put it.</summary>
    protected override void Check(ISwDocument doc, RefgeomPointRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "RefPoint", "reference point");
        FeatureFacts.VolumeChanged(result, "reference point", VolumeBefore, Volume(doc, doc.SolidBodyCount()), 0);
        var at = RefGeometry.Made(doc, result).Origin
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} has no position", new JsonObject { ["check"] = "point_position" });
        var refs = Resolved(doc, request);
        string name = result.Feature.Name ?? "the point";
        switch (request.Mode)
        {
            case PointMode.ArcCenter:
                var centre = doc.Describe(refs[0]).Origin!;
                double off = Math.Sqrt(Math.Pow(at.X - centre.X, 2) + Math.Pow(at.Y - centre.Y, 2) + Math.Pow(at.Z - centre.Z, 2));
                FeatureChecks.Near("point_position", $"the distance from {name} to the circle's centre", 0, off, RefGeometry.LengthTolerance, "mm");
                result.Checks.Add($"at the circle's centre {centre}");
                break;
            case PointMode.FaceCenter:
                var face = doc.Describe(refs[0]);
                if (face.Geometry == GeometryType.Plane && face.Normal != null && face.Origin != null)
                {
                    RefGeometry.Contains(face.Origin, face.Normal, at, name, result);
                    result.Checks.Add("on the face's plane");
                }
                break;
            default:
                for (int i = 0; i < refs.Count; i++)
                    FeatureChecks.Near("point_position", $"the distance from {name} to /entities/{i}", 0, doc.DistanceMm(refs[i], at), RefGeometry.LengthTolerance, "mm");
                result.Checks.Add(request.Mode == PointMode.Intersection ? "on both edges" : "on the edge");
                break;
        }
    }
}

/// <summary>Resolving and checking what reference geometry is built on.</summary>
public static class RefGeometry
{
    public const double LengthTolerance = 1e-6;
    public const double AngleTolerance = 1e-6;

    /// <summary>A plane, or a planar face: invalid_argument for any other face.</summary>
    public static object Plane(ReferenceService references, ISwDocument doc, string text, string field)
    {
        var (entity, kind) = references.Resolve(doc, text, field, ReferenceKind.Plane, ReferenceKind.Face);
        var info = doc.Describe(entity);
        if (kind == ReferenceKind.Face && info.Geometry != GeometryType.Plane)
            throw FeatureFacts.Invalid(field, $"{field}: this needs a plane or a planar face, and this is a {WireNames.ForEnum(info.Geometry)} face");
        if (info.Normal == null) throw FeatureFacts.Invalid(field, $"{field}: SOLIDWORKS gives no normal for it");
        return entity;
    }

    /// <summary>A straight edge or an axis.</summary>
    public static object Line(ReferenceService references, ISwDocument doc, string text, string field)
    {
        var (entity, kind) = references.Resolve(doc, text, field, ReferenceKind.Edge, ReferenceKind.Axis);
        var info = doc.Describe(entity);
        if (kind == ReferenceKind.Edge && info.Geometry != GeometryType.Line)
            throw FeatureFacts.Invalid(field, $"{field}: this needs a straight edge or an axis, and this is a {WireNames.ForEnum(info.Geometry)} edge");
        return entity;
    }

    /// <summary>The feature just made, described now.</summary>
    public static EntityInfo Made(ISwDocument doc, FeatureResult result) =>
        doc.Describe(doc.FeatureByName(result.Feature.Name ?? "")
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} is not in the part", new JsonObject { ["check"] = "feature_exists" }));

    /// <summary>The axis through <paramref name="at"/> along <paramref name="dir"/> is the line through <paramref name="point"/> along <paramref name="line"/>.</summary>
    public static void Along(Vector3 line, Point3 point, Vector3 dir, Point3 at, string name)
    {
        FeatureChecks.Near("axis_direction", $"the angle between {name} and the line it should follow", 0, FeatureChecks.AngleDeg(line, dir), AngleTolerance, "deg");
        double l = Math.Sqrt(line.X * line.X + line.Y * line.Y + line.Z * line.Z);
        double dx = at.X - point.X, dy = at.Y - point.Y, dz = at.Z - point.Z;
        double cx = dy * line.Z - dz * line.Y, cy = dz * line.X - dx * line.Z, cz = dx * line.Y - dy * line.X;
        FeatureChecks.Near("axis_position", $"the distance from {name} to the line it should follow", 0, Math.Sqrt(cx * cx + cy * cy + cz * cz) / l, LengthTolerance, "mm");
    }

    public static void Parallel(Vector3 expected, Vector3 normal, FeatureResult result) =>
        FeatureChecks.Near("plane_normal", $"the angle between {result.Feature.Name} and the plane it is parallel to", 0, FeatureChecks.AngleDeg(expected, normal), AngleTolerance, "deg");

    public static void Contains(Point3 origin, Vector3 normal, Point3 point, string what, FeatureResult result) =>
        FeatureChecks.Near("plane_contains", $"the distance from {result.Feature.Name} to {what}", 0, FeatureChecks.Offset(origin, normal, point), LengthTolerance, "mm");
}
