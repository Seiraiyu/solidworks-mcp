using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>A sketch profile's region: its area and the centroid of that area, in the sketch's own coordinates (mm).</summary>
public sealed class ProfileRegion
{
    public ProfileRegion(double areaMm2, Point2 centroid)
    {
        AreaMm2 = areaMm2;
        Centroid = centroid;
    }

    public double AreaMm2 { get; }
    public Point2 Centroid { get; }
}

/// <summary>
/// The region a sketch's closed profile encloses, for the exact checks of extrudes and revolves (design 371, Features):
/// lines chained end to end into closed loops, and whole circles. A loop inside an odd number of others is a hole. Arcs,
/// splines and open chains are not measured: the operation then checks only the sign of the volume change.
/// </summary>
public static class Profiles
{
    const double Join = 1e-6;

    public static ProfileRegion? Region(IReadOnlyList<(Point2 Start, Point2 End)> lines, IReadOnlyList<(Point2 Center, double Radius)> circles)
    {
        var loops = new List<Loop>();
        var unused = lines.ToList();
        while (unused.Count > 0)
        {
            var first = unused[0];
            unused.RemoveAt(0);
            var points = new List<Point2> { first.Start };
            var at = first.End;
            while (!Same(at, first.Start))
            {
                int next = unused.FindIndex(l => Same(l.Start, at) || Same(l.End, at));
                if (next < 0) return null;
                points.Add(at);
                at = Same(unused[next].Start, at) ? unused[next].End : unused[next].Start;
                unused.RemoveAt(next);
            }
            loops.Add(Polygon(points));
        }
        loops.AddRange(circles.Select(c => new Loop(Math.PI * c.Radius * c.Radius, c.Center, new Point2(c.Center.X + c.Radius, c.Center.Y),
            p => Distance(p, c.Center) < c.Radius)));
        if (loops.Count == 0) return null;
        double area = 0, x = 0, y = 0;
        foreach (var loop in loops)
        {
            int around = loops.Count(other => !ReferenceEquals(other, loop) && other.Contains(loop.Sample));
            double sign = around % 2 == 0 ? 1 : -1;
            area += sign * loop.Area;
            x += sign * loop.Area * loop.Centroid.X;
            y += sign * loop.Area * loop.Centroid.Y;
        }
        return area <= 0 ? null : new ProfileRegion(area, new Point2(x / area, y / area));
    }

    static Loop Polygon(List<Point2> p)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var u = p[i];
            var v = p[(i + 1) % p.Count];
            double cross = u.X * v.Y - v.X * u.Y;
            a += cross;
            cx += (u.X + v.X) * cross;
            cy += (u.Y + v.Y) * cross;
        }
        a /= 2;
        var centroid = a == 0 ? p[0] : new Point2(cx / (6 * a), cy / (6 * a));
        return new Loop(Math.Abs(a), centroid, p[0], q => Inside(p, q));
    }

    /// <summary>Ray casting: is <paramref name="q"/> inside the polygon <paramref name="p"/>.</summary>
    static bool Inside(List<Point2> p, Point2 q)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            if ((p[i].Y > q.Y) != (p[j].Y > q.Y) && q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
        }
        return inside;
    }

    static bool Same(Point2 a, Point2 b) => Distance(a, b) <= Join * Math.Max(1, Math.Max(Math.Abs(a.X), Math.Abs(a.Y)));

    static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    sealed class Loop
    {
        public Loop(double area, Point2 centroid, Point2 sample, Func<Point2, bool> contains)
        {
            Area = area;
            Centroid = centroid;
            Sample = sample;
            Contains = contains;
        }

        public double Area { get; }
        public Point2 Centroid { get; }

        /// <summary>A point on the loop itself, to test which loops it lies inside.</summary>
        public Point2 Sample { get; }

        public Func<Point2, bool> Contains { get; }
    }
}

/// <summary>
/// The geometric checks feature operations share (design 371, Model changes, Verification): the volume changed the way
/// the feature implies, an exact volume where the geometry gives one, and the directions and offsets of reference
/// geometry. Each failure is verification_failed with the check's name, what was expected and what was measured.
/// </summary>
public static class FeatureChecks
{
    /// <summary>Relative tolerance of an exact volume (SOLIDWORKS' mass properties of prismatic and revolved solids are exact to about 1e-9).</summary>
    public const double VolumeTolerance = 1e-5;

    /// <summary>The volume grew (<paramref name="sign"/> 1), shrank (-1) or stayed the same (0).</summary>
    public static void VolumeChanged(string what, double before, double after, int sign)
    {
        double tolerance = 1e-6 * Math.Max(1, Math.Abs(before));
        double delta = after - before;
        bool ok = sign > 0 ? delta > tolerance : sign < 0 ? delta < -tolerance : Math.Abs(delta) <= tolerance;
        if (ok) return;
        string expected = sign > 0 ? "more than" : sign < 0 ? "less than" : "the same as";
        throw new SwAgentException(ErrorCode.VerificationFailed,
            FormattableString.Invariant($"the {what} should leave the part's volume {expected} {before:0.###} mm³, and it is {after:0.###} mm³"),
            new JsonObject { ["check"] = "volume_change", ["expected"] = sign > 0 ? "more" : sign < 0 ? "less" : "same", ["before"] = before, ["measured"] = after });
    }

    /// <summary>The volume changed, either way (a fillet or chamfer removes material on a convex edge and adds it on a concave one).</summary>
    public static void VolumeMoved(string what, double before, double after)
    {
        if (Math.Abs(after - before) > 1e-6 * Math.Max(1, Math.Abs(before))) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            FormattableString.Invariant($"the {what} left the part's volume at {after:0.###} mm³, as it was"),
            new JsonObject { ["check"] = "volume_change", ["expected"] = "different", ["before"] = before, ["measured"] = after });
    }

    /// <summary>The volume changed by <paramref name="expected"/> mm³ (signed), within <see cref="VolumeTolerance"/>; <paramref name="why"/> says where the number comes from.</summary>
    public static void VolumeChangeIs(string what, double expected, double measured, string why)
    {
        if (Math.Abs(measured - expected) <= VolumeTolerance * Math.Max(1, Math.Abs(expected))) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            FormattableString.Invariant($"the {what} changed the volume by {measured:0.###} mm³, not the {expected:0.###} mm³ its profile gives ({why})"),
            new JsonObject { ["check"] = "volume_exact", ["expected"] = expected, ["measured"] = measured, ["why"] = why });
    }

    /// <summary>Pappus: the volume a region sweeps turning <paramref name="angleDeg"/> about the line through a and b (all in the sketch).</summary>
    public static double PappusVolume(ProfileRegion region, Point2 a, Point2 b, double angleDeg)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double distance = Math.Abs(dx * (region.Centroid.Y - a.Y) - dy * (region.Centroid.X - a.X)) / length;
        return angleDeg * Math.PI / 180 * distance * region.AreaMm2;
    }

    /// <summary>The angle between two directions taken as lines, 0 to 90 degrees.</summary>
    public static double AngleDeg(Vector3 a, Vector3 b)
    {
        double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        double cos = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
        return Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
    }

    /// <summary>How far <paramref name="point"/> is from the plane through <paramref name="origin"/> with <paramref name="normal"/>, mm.</summary>
    public static double Offset(Point3 origin, Vector3 normal, Point3 point)
    {
        double length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        return Math.Abs((point.X - origin.X) * normal.X + (point.Y - origin.Y) * normal.Y + (point.Z - origin.Z) * normal.Z) / length;
    }

    /// <summary>verification_failed unless <paramref name="measured"/> is within <paramref name="tolerance"/> of <paramref name="expected"/>.</summary>
    public static void Near(string check, string what, double expected, double measured, double tolerance, string unit)
    {
        if (Math.Abs(measured - expected) <= tolerance) return;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            FormattableString.Invariant($"{what} is {measured:0.######} {unit}, not {expected:0.######} {unit}"),
            new JsonObject { ["check"] = check, ["expected"] = expected, ["measured"] = measured });
    }
}
