using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Component placements (design 455, Components): a <see cref="Transform"/> in mm built from a request, read from and
/// written to SOLIDWORKS' 16-number transform array, and compared. The array holds the component's x, y and z axes in
/// assembly coordinates (three numbers each), then its origin in metres, then the scale (1) and three unused zeros.
/// </summary>
public static class Placement
{
    /// <summary>A position counts as unchanged within this many mm.</summary>
    public const double OriginToleranceMm = 0.001;

    /// <summary>An axis counts as unchanged when every component is within this.</summary>
    public const double AxisTolerance = 1e-6;

    /// <summary>
    /// The transform a request asks for: <paramref name="origin"/> (the assembly origin when null) and either both axes,
    /// which must be perpendicular, or neither (the assembly's own). invalid_argument naming the field otherwise.
    /// </summary>
    public static Transform Make(Point3? origin, Vector3? xAxis, Vector3? zAxis, string xField, string zField)
    {
        var t = new Transform { Origin = origin ?? new Point3() };
        if (xAxis == null && zAxis == null) return t;
        if (xAxis == null || zAxis == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"give both {xField} and {zField}, or neither (the component then keeps the assembly's axes)",
                new JsonObject { ["field"] = xAxis == null ? xField : zField });
        }
        var x = Unit(xAxis, xField);
        var z = Unit(zAxis, zField);
        double dot = x.X * z.X + x.Y * z.Y + x.Z * z.Z;
        if (Math.Abs(dot) > 1e-6)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{xField} and {zField} must be perpendicular; their dot product is {dot.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}",
                new JsonObject { ["field"] = zField });
        }
        t.XAxis = x;
        t.ZAxis = z;
        t.YAxis = new Vector3(z.Y * x.Z - z.Z * x.Y, z.Z * x.X - z.X * x.Z, z.X * x.Y - z.Y * x.X);
        return t;
    }

    /// <summary>SOLIDWORKS' transform array for <paramref name="t"/>.</summary>
    public static double[] ToArray(Transform t) => new[]
    {
        t.XAxis.X, t.XAxis.Y, t.XAxis.Z,
        t.YAxis.X, t.YAxis.Y, t.YAxis.Z,
        t.ZAxis.X, t.ZAxis.Y, t.ZAxis.Z,
        t.Origin.X / 1000, t.Origin.Y / 1000, t.Origin.Z / 1000,
        1, 0, 0, 0,
    };

    /// <summary>The transform SOLIDWORKS' array describes, origin in mm.</summary>
    public static Transform FromArray(double[] a)
    {
        if (a == null || a.Length < 12) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS returned a component transform with fewer than 12 numbers");
        return new Transform
        {
            XAxis = new Vector3(a[0], a[1], a[2]),
            YAxis = new Vector3(a[3], a[4], a[5]),
            ZAxis = new Vector3(a[6], a[7], a[8]),
            Origin = new Point3(a[9] * 1000, a[10] * 1000, a[11] * 1000),
        };
    }

    /// <summary>True when the origins differ by more than <see cref="OriginToleranceMm"/>.</summary>
    public static bool OriginMoved(Transform a, Transform b) =>
        Math.Abs(a.Origin.X - b.Origin.X) > OriginToleranceMm || Math.Abs(a.Origin.Y - b.Origin.Y) > OriginToleranceMm || Math.Abs(a.Origin.Z - b.Origin.Z) > OriginToleranceMm;

    /// <summary>True when any axis differs by more than <see cref="AxisTolerance"/> in any component.</summary>
    public static bool Rotated(Transform a, Transform b) =>
        Differs(a.XAxis, b.XAxis) || Differs(a.YAxis, b.YAxis) || Differs(a.ZAxis, b.ZAxis);

    /// <summary>True when the two placements differ at all.</summary>
    public static bool Moved(Transform a, Transform b) => OriginMoved(a, b) || Rotated(a, b);

    static bool Differs(Vector3 a, Vector3 b) =>
        Math.Abs(a.X - b.X) > AxisTolerance || Math.Abs(a.Y - b.Y) > AxisTolerance || Math.Abs(a.Z - b.Z) > AxisTolerance;

    static Vector3 Unit(Vector3 v, string field)
    {
        double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (length < 1e-9)
            throw new SwAgentException(ErrorCode.InvalidArgument, $"{field} has no length; give a direction", new JsonObject { ["field"] = field });
        return new Vector3(v.X / length, v.Y / length, v.Z / length);
    }

    /// <summary>A point given in the component's own coordinates (mm), in the assembly's: its origin plus the point along its axes.</summary>
    public static Point3 ToAssembly(Transform t, Point3 p) => new Point3(
        t.Origin.X + p.X * t.XAxis.X + p.Y * t.YAxis.X + p.Z * t.ZAxis.X,
        t.Origin.Y + p.X * t.XAxis.Y + p.Y * t.YAxis.Y + p.Z * t.ZAxis.Y,
        t.Origin.Z + p.X * t.XAxis.Z + p.Y * t.YAxis.Z + p.Z * t.ZAxis.Z);

    /// <summary>A direction given in the component's own coordinates, in the assembly's (turned, not moved).</summary>
    public static Vector3 ToAssembly(Transform t, Vector3 v) => new Vector3(
        Clean(v.X * t.XAxis.X + v.Y * t.YAxis.X + v.Z * t.ZAxis.X),
        Clean(v.X * t.XAxis.Y + v.Y * t.YAxis.Y + v.Z * t.ZAxis.Y),
        Clean(v.X * t.XAxis.Z + v.Y * t.YAxis.Z + v.Z * t.ZAxis.Z));

    /// <summary>A point given in the assembly's coordinates (mm), in the component's own: the inverse of <see cref="ToAssembly(Transform, Point3)"/>.</summary>
    public static Point3 ToComponent(Transform t, Point3 p)
    {
        double dx = p.X - t.Origin.X, dy = p.Y - t.Origin.Y, dz = p.Z - t.Origin.Z;
        return new Point3(
            dx * t.XAxis.X + dy * t.XAxis.Y + dz * t.XAxis.Z,
            dx * t.YAxis.X + dy * t.YAxis.Y + dz * t.YAxis.Z,
            dx * t.ZAxis.X + dy * t.ZAxis.Y + dz * t.ZAxis.Z);
    }

    /// <summary>The axis-aligned box, in the assembly, around a box given in the component's own coordinates: the box of its eight corners moved.</summary>
    public static Box3 ToAssembly(Transform t, Box3 b)
    {
        double[] xs = { b.Min.X, b.Max.X }, ys = { b.Min.Y, b.Max.Y }, zs = { b.Min.Z, b.Max.Z };
        Point3? lo = null, hi = null;
        foreach (double x in xs)
        {
            foreach (double y in ys)
            {
                foreach (double z in zs)
                {
                    var p = ToAssembly(t, new Point3(x, y, z));
                    lo = lo == null ? p : new Point3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                    hi = hi == null ? p : new Point3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                }
            }
        }
        return new Box3 { Min = lo!, Max = hi! };
    }

    /// <summary>
    /// Moves a description made in the component's own coordinates into the assembly's (design 455, Assembly references):
    /// its point, normal, axis and box. Lengths, areas, radii and names do not change.
    /// </summary>
    public static void MoveToAssembly(Transform t, EntityInfo e)
    {
        if (e.Origin != null) e.Origin = ToAssembly(t, e.Origin);
        if (e.Normal != null) e.Normal = ToAssembly(t, e.Normal);
        if (e.Axis != null) e.Axis = ToAssembly(t, e.Axis);
        if (e.Box != null) e.Box = ToAssembly(t, e.Box);
    }

    /// <summary>Rounds away floating-point dust (1e-17 becomes 0) so directions read cleanly.</summary>
    static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : v;
}
