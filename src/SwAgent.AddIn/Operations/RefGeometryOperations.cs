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

    public static void Parallel(Vector3 expected, Vector3 normal, FeatureResult result) =>
        FeatureChecks.Near("plane_normal", $"the angle between {result.Feature.Name} and the plane it is parallel to", 0, FeatureChecks.AngleDeg(expected, normal), AngleTolerance, "deg");

    public static void Contains(Point3 origin, Vector3 normal, Point3 point, string what, FeatureResult result) =>
        FeatureChecks.Near("plane_contains", $"the distance from {result.Feature.Name} to {what}", 0, FeatureChecks.Offset(origin, normal, point), LengthTolerance, "mm");
}
