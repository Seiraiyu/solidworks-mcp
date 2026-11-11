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
}
