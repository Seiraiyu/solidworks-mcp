using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>A hole's axis: a point on it and its unit direction, mm in the assembly, and the radius of its first cylinder.</summary>
public sealed class HoleAxis
{
    public HoleAxis(Point3 point, Vector3 axis, double radiusMm)
    {
        Point = point;
        Axis = axis;
        RadiusMm = radiusMm;
    }

    public Point3 Point { get; }

    public Vector3 Axis { get; }

    public double RadiusMm { get; }
}

/// <summary>Where a new instance of <see cref="Seed"/> must land.</summary>
public sealed class ExpectedInstance
{
    public ExpectedInstance(string seed, Transform at)
    {
        Seed = seed;
        At = at;
    }

    public string Seed { get; }

    public Transform At { get; }
}

/// <summary>One way SOLIDWORKS may read a pattern request (which way along an edge, which way round an axis), and the instances it implies.</summary>
public sealed class PatternReading
{
    public PatternReading(string sense, IReadOnlyList<ExpectedInstance> instances)
    {
        Sense = sense;
        Instances = instances;
    }

    public string Sense { get; }

    public IReadOnlyList<ExpectedInstance> Instances { get; }
}

/// <summary>
/// Where the instances of a component pattern must land (design 455, Component patterns), worked out from the seeds'
/// placements before SOLIDWORKS is asked, and the instances it made paired with those places. Millimetres and degrees;
/// tolerances are the mates' (0.001 mm, 0.01 degrees).
/// </summary>
public static class PatternGeometry
{
    public const double OriginToleranceMm = 0.001;

    public const double TurnToleranceDeg = 0.01;

    public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    static Vector3 Scale(Vector3 v, double k) => new Vector3(v.X * k, v.Y * k, v.Z * k);

    static Vector3 Sum(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    static Vector3 Minus(Point3 a, Point3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    static Point3 Plus(Point3 p, Vector3 v) => new Point3(p.X + v.X, p.Y + v.Y, p.Z + v.Z);

    public static double Distance(Point3 a, Point3 b)
    {
        var d = Minus(a, b);
        return Math.Sqrt(Dot(d, d));
    }

    public static Vector3 Unit(Vector3 v)
    {
        double length = Math.Sqrt(Dot(v, v));
        if (length < 1e-12) throw new ArgumentException("a direction has no length");
        return Scale(v, 1 / length);
    }

    /// <summary><paramref name="v"/> turned by <paramref name="deg"/> about <paramref name="axis"/>, right-handed (Rodrigues).</summary>
    public static Vector3 Turn(Vector3 v, Vector3 axis, double deg)
    {
        var k = Unit(axis);
        double t = deg * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t), kv = Dot(k, v);
        var kxv = Cross(k, v);
        return new Vector3(
            v.X * c + kxv.X * s + k.X * kv * (1 - c),
            v.Y * c + kxv.Y * s + k.Y * kv * (1 - c),
            v.Z * c + kxv.Z * s + k.Z * kv * (1 - c));
    }

    public static Transform Shift(Transform t, Vector3 by) =>
        new Transform { Origin = Plus(t.Origin, by), XAxis = t.XAxis, YAxis = t.YAxis, ZAxis = t.ZAxis };

    public static Transform Turn(Transform t, Point3 through, Vector3 axis, double deg) => new Transform
    {
        Origin = Plus(through, Turn(Minus(t.Origin, through), axis, deg)),
        XAxis = Turn(t.XAxis, axis, deg),
        YAxis = Turn(t.YAxis, axis, deg),
        ZAxis = Turn(t.ZAxis, axis, deg),
    };

    /// <summary>The direction a linear pattern runs along: an edge's or axis's direction, a plane's or planar face's normal; null otherwise.</summary>
    public static Vector3? DirectionOf(EntityInfo e)
    {
        if (e.Kind == ReferenceKind.Axis || e.Geometry == GeometryType.Line) return e.Axis;
        if (e.Geometry == GeometryType.Plane) return e.Normal;
        return null;
    }

    /// <summary>The line a circular pattern turns about: an axis, a straight or circular edge, a cylindrical or conical face; null otherwise.</summary>
    public static (Point3 Through, Vector3 Axis)? AxisOf(EntityInfo e)
    {
        if (e.Origin == null || e.Axis == null) return null;
        bool line = e.Kind == ReferenceKind.Axis || e.Geometry == GeometryType.Line || e.Geometry == GeometryType.Circle
            || e.Geometry == GeometryType.Cylinder || e.Geometry == GeometryType.Cone;
        return line ? (e.Origin, e.Axis) : ((Point3, Vector3)?)null;
    }

    static string Sense(int sign) => sign > 0 ? "along" : "against";

    /// <summary>Every new instance of a linear pattern, once for each sense SOLIDWORKS may take along each direction.</summary>
    public static IReadOnlyList<PatternReading> Linear(IReadOnlyList<(string Name, Transform At)> seeds, Vector3 direction, double spacingMm, int count,
        Vector3? direction2, double spacingMm2, int count2)
    {
        var u1 = Unit(direction);
        var u2 = direction2 == null ? null : Unit(direction2);
        int rows = u2 == null ? 1 : count2;
        var readings = new List<PatternReading>();
        foreach (int a in new[] { 1, -1 })
        {
            foreach (int b in u2 == null ? new[] { 1 } : new[] { 1, -1 })
            {
                var list = new List<ExpectedInstance>();
                foreach (var (name, at) in seeds)
                {
                    for (int i = 0; i < count; i++)
                    {
                        for (int j = 0; j < rows; j++)
                        {
                            if (i == 0 && j == 0) continue;
                            var by = Scale(u1, a * spacingMm * i);
                            if (u2 != null) by = Sum(by, Scale(u2, b * spacingMm2 * j));
                            list.Add(new ExpectedInstance(name, Shift(at, by)));
                        }
                    }
                }
                readings.Add(new PatternReading(u2 == null ? Sense(a) : $"{Sense(a)}/{Sense(b)}", list));
            }
        }
        return readings;
    }

    /// <summary>The angle from one instance to the next: 360 shared by count, a smaller angle by count - 1 gaps, or the angle itself without equal spacing.</summary>
    public static double CircularStep(int count, double angleDeg, bool equalSpacing) =>
        !equalSpacing ? angleDeg : Math.Abs(angleDeg - 360) < 1e-9 ? 360.0 / count : angleDeg / (count - 1);

    /// <summary>Every new instance of a circular pattern, turned each way round the axis.</summary>
    public static IReadOnlyList<PatternReading> Circular(IReadOnlyList<(string Name, Transform At)> seeds, Point3 through, Vector3 axis, int count, double stepDeg)
    {
        var readings = new List<PatternReading>();
        foreach (int sign in new[] { 1, -1 })
        {
            var list = new List<ExpectedInstance>();
            foreach (var (name, at) in seeds)
            {
                for (int i = 1; i < count; i++) list.Add(new ExpectedInstance(name, Turn(at, through, axis, sign * stepDeg * i)));
            }
            readings.Add(new PatternReading(Sense(sign), list));
        }
        return readings;
    }

    /// <summary>The largest angle between corresponding axes of two placements, degrees.</summary>
    public static double TurnDeg(Transform a, Transform b)
    {
        double worst = 0;
        foreach (var (u, v) in new[] { (a.XAxis, b.XAxis), (a.YAxis, b.YAxis), (a.ZAxis, b.ZAxis) })
        {
            double cos = Math.Max(-1, Math.Min(1, Dot(Unit(u), Unit(v))));
            worst = Math.Max(worst, Math.Acos(cos) * 180 / Math.PI);
        }
        return worst;
    }

    /// <summary>
    /// The instances SOLIDWORKS made, each paired with the nearest place one should be, under the reading they fit best.
    /// verification_failed when the count differs, or when one is further than 0.001 mm (or turned more than 0.01
    /// degrees, with <paramref name="checkAxes"/>) from its place.
    /// </summary>
    public static (PatternReading Reading, List<PatternInstance> Instances) Match(IReadOnlyList<PatternReading> readings,
        IReadOnlyList<(string Name, Transform At)> actual, bool checkAxes)
    {
        int expected = readings[0].Instances.Count;
        if (actual.Count != expected)
            throw Failed("instances", $"the pattern made {actual.Count} instances, not {expected}", expected, actual.Count);
        (PatternReading Reading, List<PatternInstance> Instances, double Worst)? best = null;
        foreach (var reading in readings)
        {
            var paired = Pair(reading.Instances, actual, checkAxes);
            double worst = paired.Count == 0 ? 0 : paired.Max(p => Math.Max(p.OffsetMm / OriginToleranceMm, (p.TurnDeg ?? 0) / TurnToleranceDeg));
            if (best == null || worst < best.Value.Worst) best = (reading, paired, worst);
        }
        var (chosen, instances, _) = best!.Value;
        var off = instances.FirstOrDefault(i => i.OffsetMm > OriginToleranceMm);
        if (off != null)
        {
            throw Failed("instance_offset_mm", $"{off.Name} is at {off.Origin}, {Mm(off.OffsetMm)} mm from {off.Expected} where it should be",
                0.0, off.OffsetMm, off.Name, off.Expected);
        }
        var turned = instances.FirstOrDefault(i => (i.TurnDeg ?? 0) > TurnToleranceDeg);
        if (turned != null)
            throw Failed("instance_turn_deg", $"{turned.Name} is turned {Mm(turned.TurnDeg ?? 0)} degrees from how it should be", 0.0, turned.TurnDeg, turned.Name, turned.Expected);
        return (chosen, instances);
    }

    /// <summary>Each actual instance with the nearest unused expected place.</summary>
    static List<PatternInstance> Pair(IReadOnlyList<ExpectedInstance> expected, IReadOnlyList<(string Name, Transform At)> actual, bool checkAxes)
    {
        var free = expected.ToList();
        var list = new List<PatternInstance>();
        foreach (var (name, at) in actual)
        {
            var nearest = free.OrderBy(e => Distance(e.At.Origin, at.Origin)).First();
            free.Remove(nearest);
            list.Add(new PatternInstance
            {
                Name = name, Seed = nearest.Seed, Origin = at.Origin, Expected = nearest.At.Origin,
                OffsetMm = Distance(nearest.At.Origin, at.Origin), TurnDeg = checkAxes ? TurnDeg(nearest.At, at) : (double?)null,
            });
        }
        return list;
    }

    /// <summary>The distance from <paramref name="p"/> to the line through <paramref name="through"/> along <paramref name="axis"/>, mm.</summary>
    public static double LineOffset(Point3 through, Vector3 axis, Point3 p)
    {
        var c = Cross(Minus(p, through), Unit(axis));
        return Math.Sqrt(Dot(c, c));
    }

    /// <summary>One axis per hole: the cylinders among <paramref name="faces"/>, coaxial ones (a counterbore's two) merged, parallel to the first.</summary>
    public static IReadOnlyList<HoleAxis> HoleAxes(IEnumerable<EntityInfo> faces)
    {
        var holes = new List<HoleAxis>();
        foreach (var f in faces.Where(f => f.Geometry == GeometryType.Cylinder && f.Origin != null && f.Axis != null))
        {
            var axis = Unit(f.Axis!);
            if (holes.Count > 0 && Math.Abs(Math.Abs(Dot(holes[0].Axis, axis)) - 1) > 1e-9) continue;
            if (holes.Any(h => LineOffset(h.Point, h.Axis, f.Origin!) <= OriginToleranceMm)) continue;
            holes.Add(new HoleAxis(f.Origin!, holes.Count > 0 ? holes[0].Axis : axis, f.RadiusMm ?? 0));
        }
        return holes;
    }

    /// <summary>
    /// A feature pattern's instances: each seed sits on the hole whose axis is nearest it, at some distance from that
    /// axis and some height along it; every other hole must get exactly one instance of each seed at the same distance
    /// and height, within 0.001 mm. Orientation is not compared: a circular driver turns the instances.
    /// </summary>
    public static List<PatternInstance> MatchHoles(IReadOnlyList<(string Name, Transform At)> seeds, IReadOnlyList<HoleAxis> holes, IReadOnlyList<(string Name, Transform At)> actual)
    {
        var places = new List<(string Seed, HoleAxis Hole, Point3 Foot, double Radial, double Axial, Point3 Expected)>();
        foreach (var (name, at) in seeds)
        {
            var own = holes.OrderBy(h => LineOffset(h.Point, h.Axis, at.Origin)).First();
            double radial = LineOffset(own.Point, own.Axis, at.Origin), axial = Dot(Minus(at.Origin, own.Point), own.Axis);
            foreach (var h in holes.Where(h => !ReferenceEquals(h, own)))
            {
                var foot = Plus(h.Point, Scale(h.Axis, Dot(Minus(own.Point, h.Point), h.Axis)));
                places.Add((name, h, foot, radial, axial, Plus(foot, Minus(at.Origin, own.Point))));
            }
        }
        if (actual.Count != places.Count)
            throw Failed("instances", $"the pattern made {actual.Count} instances, not {places.Count} (one for each other hole, for each seed)", places.Count, actual.Count);
        var list = new List<PatternInstance>();
        foreach (var (name, at) in actual)
        {
            var scored = places.Select(p => (Place: p, Error: Math.Max(
                Math.Abs(LineOffset(p.Foot, p.Hole.Axis, at.Origin) - p.Radial),
                Math.Abs(Dot(Minus(at.Origin, p.Foot), p.Hole.Axis) - p.Axial)))).OrderBy(s => s.Error).ToList();
            // Counts are equal, so a place is always left while an instance is.
            var (place, error) = scored[0];
            if (error > OriginToleranceMm)
            {
                var near = place.Expected;
                throw Failed("instance_offset_mm",
                    $"{name} is at {at.Origin}, which is not on a hole the pattern still owes an instance (nearest place {near}, {Mm(error)} mm off): a hole has two instances or none, or one sits at the wrong height",
                    0.0, error, name, near);
            }
            places.Remove(place);
            list.Add(new PatternInstance { Name = name, Seed = place.Seed, Origin = at.Origin, Expected = place.Expected, OffsetMm = error });
        }
        return list;
    }

    static string Mm(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    static SwAgentException Failed(string check, string message, object? expected, object? measured, string? instance = null, Point3? at = null)
    {
        var details = new JsonObject { ["check"] = check, ["expected"] = expected, ["measured"] = measured };
        if (instance != null) details["instance"] = instance;
        if (at != null) details["expected_origin"] = at.ToString();
        return new SwAgentException(ErrorCode.VerificationFailed, message, details);
    }
}
