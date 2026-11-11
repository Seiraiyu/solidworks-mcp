using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>A bend as the development checks read it: its angle, inner radius and allowance, its width, and whether it is whole.</summary>
public sealed class DevelopedBend
{
    public DevelopedBend(string name, double angleDeg, double radiusMm, BendAllowance allowance, double? widthMm, bool whole)
    {
        Name = name;
        AngleDeg = angleDeg;
        RadiusMm = radiusMm;
        Allowance = allowance;
        WidthMm = widthMm;
        Whole = whole;
    }

    public string Name { get; }
    public double AngleDeg { get; }
    public double RadiusMm { get; }
    public BendAllowance Allowance { get; }

    /// <summary>How wide it is along its axis, mm (its cylindrical faces' extent); null when that cannot be measured.</summary>
    public double? WidthMm { get; }

    /// <summary>True when nothing is cut across it: its cylindrical faces are one inner and one outer, each its whole width.</summary>
    public bool Whole { get; }
}

/// <summary>
/// What developing bends (flattening or unfolding them) or forming them (folding, or a sketched bend) changes a part's
/// volume by: the bend arithmetic's w·T·(BA − θ·(R + T/2)) summed over the bends, flattening; the same the other way,
/// folding. Exact when every bend is whole; when one is cut across, the cut takes the same share of its folded and flat
/// material, so the change lies between nothing and the whole bends' sum.
/// </summary>
public sealed class BendVolumeChange
{
    public BendVolumeChange(double expectedMm3, bool exact, string why)
    {
        ExpectedMm3 = expectedMm3;
        Exact = exact;
        Why = why;
    }

    /// <summary>The change with every bend whole, mm³ (signed).</summary>
    public double ExpectedMm3 { get; }

    /// <summary>False when a bend is cut across: the change is then bounded by 0 and <see cref="ExpectedMm3"/>.</summary>
    public bool Exact { get; }

    /// <summary>Where the number comes from, for a check line.</summary>
    public string Why { get; }

    /// <summary>The measured change is the expected one within <paramref name="toleranceMm3"/>, or, not exact, between 0 and it.</summary>
    public bool Holds(double measuredMm3, double toleranceMm3)
    {
        if (Exact) return Math.Abs(measuredMm3 - ExpectedMm3) <= toleranceMm3;
        return measuredMm3 >= Math.Min(0, ExpectedMm3) - toleranceMm3 && measuredMm3 <= Math.Max(0, ExpectedMm3) + toleranceMm3;
    }
}

public static class BendDevelopment
{
    /// <summary>
    /// The volume change of flattening (<paramref name="flatten"/>) or folding these bends of a sheet <paramref name="thicknessMm"/>
    /// thick; null when a bend's width is unknown or its allowance comes from a table, so nothing can be compared.
    /// </summary>
    public static BendVolumeChange? Develop(IReadOnlyList<DevelopedBend> bends, double thicknessMm, bool flatten)
    {
        if (bends.Any(b => b.WidthMm == null || (b.Allowance.KFactor == null && b.Allowance.AllowanceMm == null && b.Allowance.DeductionMm == null))) return null;
        double sum = bends.Sum(b => BendArithmetic.FlattenChangeMm3(b.WidthMm!.Value, b.AngleDeg, b.RadiusMm, thicknessMm, b.Allowance));
        double expected = flatten ? sum : -sum;
        var cut = bends.Where(b => !b.Whole).Select(b => b.Name).ToList();
        string way = flatten ? "developing" : "folding";
        string why = FormattableString.Invariant($"{way} {bends.Count} bends: the sum of width × {thicknessMm:0.####} mm × (allowance − angle × (radius + half the thickness))");
        if (cut.Count > 0) why += $"; {string.Join(", ", cut)} cut across, so between nothing and that";
        return new BendVolumeChange(expected, cut.Count == 0, why);
    }
}

/// <summary>The corner a solid's bend edge makes, for sheetmetal.convert: SOLIDWORKS bends only an inner (concave) corner.</summary>
public static class ConvertGeometry
{
    /// <summary>
    /// True when the edge through <paramref name="onEdge"/> along <paramref name="axis"/> is an inner corner between face A
    /// (outward normal <paramref name="normalA"/>, a point <paramref name="insideA"/> inside it) and face B: each face runs
    /// from the edge towards the side the other faces, so the material fills more than half the turn around it.
    /// </summary>
    public static bool Concave(Point3 onEdge, Vector3 axis, Vector3 normalA, Point3 insideA, Vector3 normalB, Point3 insideB)
    {
        var intoA = Across(onEdge, axis, insideA);
        var intoB = Across(onEdge, axis, insideB);
        if (intoA == null || intoB == null) return false;
        return Dot(normalA, intoB) > 1e-6 && Dot(normalB, intoA) > 1e-6;
    }

    /// <summary>The unit direction from the edge to <paramref name="p"/>, square to the edge; null when p is on its line.</summary>
    static Vector3? Across(Point3 onEdge, Vector3 axis, Point3 p)
    {
        double dx = p.X - onEdge.X, dy = p.Y - onEdge.Y, dz = p.Z - onEdge.Z;
        double along = dx * axis.X + dy * axis.Y + dz * axis.Z;
        dx -= along * axis.X;
        dy -= along * axis.Y;
        dz -= along * axis.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return length < 1e-9 ? null : new Vector3(dx / length, dy / length, dz / length);
    }

    static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
