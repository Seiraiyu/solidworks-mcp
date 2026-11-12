using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>What a mate's side is, for which pairs a mate type joins and what it measures.</summary>
public enum MateShape
{
    Planar,
    Linear,
    Point,
    Cylindrical,
    Conical,
    Spherical,
    Circular,
    Component,
    Other,
}

/// <summary>
/// The standard mates' geometry (design 455, Mates): which pairs of entities each type joins, checked before SOLIDWORKS
/// is asked, and the condition each imposes, measured between the two sides (assembly coordinates, mm and degrees)
/// after the solve. Tolerances 0.001 mm and 0.01 degrees. Pure arithmetic: covered by unit tests.
/// </summary>
public static class MateGeometry
{
    public const double DistanceToleranceMm = 0.001;

    public const double AngleToleranceDeg = 0.01;

    static readonly MateShape[] Flat = { MateShape.Planar, MateShape.Linear, MateShape.Point };
    static readonly MateShape[] Directed = { MateShape.Planar, MateShape.Linear, MateShape.Cylindrical };
    static readonly MateShape[] Round = { MateShape.Cylindrical, MateShape.Conical, MateShape.Circular, MateShape.Linear };
    static readonly MateShape[] Curved = { MateShape.Cylindrical, MateShape.Conical, MateShape.Spherical };
    static readonly MateShape[] Gauged = { MateShape.Planar, MateShape.Point };
    static readonly MateShape[] Angled = { MateShape.Planar, MateShape.Linear };

    public static MateShape ShapeOf(EntityInfo e) => e.Kind switch
    {
        ReferenceKind.Component => MateShape.Component,
        ReferenceKind.Plane => MateShape.Planar,
        ReferenceKind.Axis => MateShape.Linear,
        ReferenceKind.Point or ReferenceKind.Vertex => MateShape.Point,
        _ => e.Geometry switch
        {
            GeometryType.Plane => MateShape.Planar,
            GeometryType.Line => MateShape.Linear,
            GeometryType.Cylinder => MateShape.Cylindrical,
            GeometryType.Cone => MateShape.Conical,
            GeometryType.Sphere => MateShape.Spherical,
            GeometryType.Circle => MateShape.Circular,
            GeometryType.Point => MateShape.Point,
            _ => MateShape.Other,
        },
    };

    /// <summary>What a type joins, in words.</summary>
    public static string Accepts(MateType type) => type switch
    {
        MateType.Coincident => "two planes, planar faces, straight edges, axes, vertices or points",
        MateType.Parallel or MateType.Perpendicular => "two planes, planar faces, straight edges, axes or cylinders",
        MateType.Tangent => "a cylinder, cone or sphere with a plane, planar face, cylinder, cone or sphere",
        MateType.Concentric => "two cylinders, cones or circular edges, or one of them with an axis or straight edge",
        MateType.Distance => "two planes, planar faces, vertices or points",
        MateType.Angle => "two planes, planar faces, straight edges or axes",
        _ => "two components",
    };

    /// <summary>Null when a mate of this type joins the pair; otherwise why not, naming what it joins.</summary>
    public static string? Refusal(MateType type, MateShape a, MateShape b)
    {
        bool ok = type switch
        {
            MateType.Coincident => Flat.Contains(a) && Flat.Contains(b),
            MateType.Parallel or MateType.Perpendicular => Directed.Contains(a) && Directed.Contains(b),
            MateType.Tangent => (Curved.Contains(a) && (b == MateShape.Planar || Curved.Contains(b))) || (Curved.Contains(b) && a == MateShape.Planar),
            MateType.Concentric => Round.Contains(a) && Round.Contains(b) && !(a == MateShape.Linear && b == MateShape.Linear),
            MateType.Distance => Gauged.Contains(a) && Gauged.Contains(b),
            MateType.Angle => Angled.Contains(a) && Angled.Contains(b),
            _ => a == MateShape.Component && b == MateShape.Component,
        };
        return ok ? null : $"a {WireNames.ForEnum(type)} mate joins {Accepts(type)}; these are a {WireNames.ForEnum(a)} and a {WireNames.ForEnum(b)}";
    }

    /// <summary>
    /// The conditions a mate of <paramref name="type"/> imposes, measured between <paramref name="a"/> and
    /// <paramref name="b"/>; <paramref name="closest"/> is SOLIDWORKS' closest distance between them, asked only when
    /// needed. Lock is measured by <see cref="Locked"/>.
    /// </summary>
    public static IEnumerable<MateMeasurement> Measure(MateType type, EntityInfo a, EntityInfo b, double? distanceMm, double? angleDeg, Func<double> closest)
    {
        var sa = ShapeOf(a);
        var sb = ShapeOf(b);
        bool mixed = (sa == MateShape.Planar) != (sb == MateShape.Planar);
        switch (type)
        {
            case MateType.Coincident:
                yield return Mm("distance_mm", 0, Separation(a, b, closest));
                if (sa != MateShape.Point && sb != MateShape.Point)
                    yield return Deg("angle_deg", mixed ? 90 : 0, FeatureChecks.AngleDeg(DirectionOf(a), DirectionOf(b)));
                break;
            case MateType.Parallel:
                yield return Deg("angle_deg", mixed ? 90 : 0, FeatureChecks.AngleDeg(DirectionOf(a), DirectionOf(b)));
                break;
            case MateType.Perpendicular:
                yield return Deg("angle_deg", mixed ? 0 : 90, FeatureChecks.AngleDeg(DirectionOf(a), DirectionOf(b)));
                break;
            case MateType.Tangent:
                yield return Mm("distance_mm", 0, Touching(a, b, closest));
                break;
            case MateType.Concentric:
                var (oa, xa) = AxisOf(a);
                var (ob, xb) = AxisOf(b);
                yield return Deg("angle_deg", 0, FeatureChecks.AngleDeg(xa, xb));
                yield return Mm("axis_offset_mm", 0, LineOffset(oa, xa, ob));
                break;
            case MateType.Distance:
                yield return Mm("distance_mm", distanceMm ?? 0, Separation(a, b, closest));
                break;
            case MateType.Angle:
                double full = EntityFilter.AngleDeg(DirectionOf(a), DirectionOf(b));
                double want = angleDeg ?? 0;
                yield return Deg("angle_deg", want, Math.Abs(full - want) <= Math.Abs(180 - full - want) ? full : 180 - full);
                break;
        }
    }

    /// <summary>A lock holds when the second component sits where it sat relative to the first: offset in mm, turn in degrees.</summary>
    public static IEnumerable<MateMeasurement> Locked(Transform aBefore, Transform bBefore, Transform aAfter, Transform bAfter)
    {
        var before = InFrame(aBefore, bBefore);
        var after = InFrame(aAfter, bAfter);
        double dx = before.Origin.X - after.Origin.X, dy = before.Origin.Y - after.Origin.Y, dz = before.Origin.Z - after.Origin.Z;
        yield return Mm("relative_offset_mm", 0, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        yield return Deg("relative_turn_deg", 0, Math.Max(EntityFilter.AngleDeg(before.XAxis, after.XAxis), EntityFilter.AngleDeg(before.ZAxis, after.ZAxis)));
    }

    /// <summary>The first measurement outside its tolerance, or null when every one holds.</summary>
    public static MateMeasurement? Failing(IEnumerable<MateMeasurement> measured) =>
        measured.FirstOrDefault(m => !(Math.Abs(m.Measured - m.Expected) <= m.Tolerance));

    /// <summary>A measurement in words: "distance 0.42 mm, expected 0".</summary>
    public static string Say(MateMeasurement m)
    {
        bool mm = m.Condition.EndsWith("_mm", StringComparison.Ordinal);
        string stem = m.Condition.Substring(0, m.Condition.LastIndexOf('_')).Replace('_', ' ');
        return $"{stem} {F(m.Measured)}{(mm ? " mm" : "°")}, expected {F(m.Expected)}";
    }

    /// <summary>
    /// How far apart two sides are: a plane's offset to a parallel plane, a line or a point (a mate treats a face as its
    /// whole plane, so two faces need not overlap); otherwise SOLIDWORKS' closest distance.
    /// </summary>
    public static double Separation(EntityInfo a, EntityInfo b, Func<double> closest)
    {
        var plane = ShapeOf(a) == MateShape.Planar ? a : ShapeOf(b) == MateShape.Planar ? b : null;
        var other = plane == a ? b : a;
        if (plane?.Normal != null && plane.Origin != null && other.Origin != null)
        {
            var s = ShapeOf(other);
            bool parallelPlane = s == MateShape.Planar && other.Normal != null && FeatureChecks.AngleDeg(plane.Normal, other.Normal) <= AngleToleranceDeg;
            if (s == MateShape.Point || s == MateShape.Linear || parallelPlane) return FeatureChecks.Offset(plane.Origin, plane.Normal, other.Origin);
        }
        return closest();
    }

    /// <summary>
    /// How far two tangent sides are from touching, treating each face as its whole surface as a mate does: a cylinder
    /// lying along a plane (its axis's offset less its radius), a sphere and a plane (its centre's offset less its
    /// radius), two parallel cylinders (their axes' distance against the sum or difference of the radii, whichever is
    /// nearer). Otherwise SOLIDWORKS' closest distance, which is between the finite faces (live 4.C case 17: a pin laid
    /// on a plate's top plane past the top face's edge measured 30.7 mm by closest distance).
    /// </summary>
    public static double Touching(EntityInfo a, EntityInfo b, Func<double> closest)
    {
        var plane = ShapeOf(a) == MateShape.Planar ? a : ShapeOf(b) == MateShape.Planar ? b : null;
        var other = plane == a ? b : a;
        if (plane?.Normal != null && plane.Origin != null && other.Origin != null && other.RadiusMm is double r)
        {
            var s = ShapeOf(other);
            if (s == MateShape.Spherical) return Math.Abs(FeatureChecks.Offset(plane.Origin, plane.Normal, other.Origin) - r);
            if (s == MateShape.Cylindrical && other.Axis != null && Math.Abs(EntityFilter.AngleDeg(plane.Normal, other.Axis) - 90) <= AngleToleranceDeg)
                return Math.Abs(FeatureChecks.Offset(plane.Origin, plane.Normal, other.Origin) - r);
        }
        if (ShapeOf(a) == MateShape.Cylindrical && ShapeOf(b) == MateShape.Cylindrical
            && a.Origin != null && a.Axis != null && b.Origin != null && b.Axis != null && a.RadiusMm is double ra && b.RadiusMm is double rb
            && FeatureChecks.AngleDeg(a.Axis, b.Axis) <= AngleToleranceDeg)
        {
            double d = LineOffset(a.Origin, a.Axis, b.Origin);
            return Math.Min(Math.Abs(d - (ra + rb)), Math.Abs(d - Math.Abs(ra - rb)));
        }
        return closest();
    }

    /// <summary>The distance from <paramref name="point"/> to the line through <paramref name="origin"/> along <paramref name="axis"/>, mm.</summary>
    public static double LineOffset(Point3 origin, Vector3 axis, Point3 point)
    {
        double l = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        double dx = point.X - origin.X, dy = point.Y - origin.Y, dz = point.Z - origin.Z;
        double cx = dy * axis.Z - dz * axis.Y, cy = dz * axis.X - dx * axis.Z, cz = dx * axis.Y - dy * axis.X;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz) / l;
    }

    static Vector3 DirectionOf(EntityInfo e) =>
        (ShapeOf(e) == MateShape.Planar ? e.Normal : e.Axis)
        ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS described the {WireNames.ForEnum(e.Kind)} {e.Name ?? e.Reference} without a direction, so the mate cannot be measured");

    static (Point3 Origin, Vector3 Axis) AxisOf(EntityInfo e) =>
        e.Origin != null && e.Axis != null
            ? (e.Origin, e.Axis)
            : throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS described the {WireNames.ForEnum(e.Kind)} {e.Name ?? e.Reference} without an axis, so the mate cannot be measured");

    static Transform InFrame(Transform frame, Transform t) => new Transform
    {
        Origin = Placement.ToComponent(frame, t.Origin),
        XAxis = Local(frame, t.XAxis),
        YAxis = Local(frame, t.YAxis),
        ZAxis = Local(frame, t.ZAxis),
    };

    static Vector3 Local(Transform f, Vector3 v) => new Vector3(Dot(v, f.XAxis), Dot(v, f.YAxis), Dot(v, f.ZAxis));

    static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    static MateMeasurement Mm(string condition, double expected, double measured) =>
        new MateMeasurement { Condition = condition, Expected = expected, Measured = measured, Tolerance = DistanceToleranceMm };

    static MateMeasurement Deg(string condition, double expected, double measured) =>
        new MateMeasurement { Condition = condition, Expected = expected, Measured = measured, Tolerance = AngleToleranceDeg };

    static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
