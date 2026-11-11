using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// Where a flange's material goes (design 454, Flanges and hems), as the bend arithmetic's companion. An edge flange is
/// worked out in its cross-section: the sheet lies behind the edge, its face (the one the edge bounds, whose normal the
/// flange rises along) at the top; the flange leaves the edge turning angle θ towards that normal, bent at inner radius R,
/// T thick. The position puts the virtual sharps: material inside puts the outer one on the edge, material outside the
/// inner one, and bend outside starts the bend at the edge. The profile the add-in draws runs from the edge, in the
/// flange's direction, to the flange's end, so its height follows from the length asked and where it is measured from.
/// Millimetres and degrees.
/// </summary>
public static class FlangeGeometry
{
    static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>An edge flange's cross-section; null when the length asked leaves no straight flange after the bend.</summary>
    public static EdgeFlangeSection? EdgeFlange(double angleDeg, double radiusMm, double thicknessMm, double lengthMm, FlangeLengthFrom from, BendPosition position)
    {
        double theta = Rad(angleDeg), t = Math.Tan(theta / 2), r = radiusMm, th = thicknessMm;
        double outerSharp = position switch
        {
            BendPosition.MaterialOutside => th * t,
            BendPosition.BendOutside => (r + th) * t,
            _ => 0,
        };
        double innerSharp = outerSharp - th * t;
        double inner = from switch
        {
            FlangeLengthFrom.InnerVirtualSharp => lengthMm,
            FlangeLengthFrom.Tangent => lengthMm + r * t,
            _ => lengthMm - th * t,
        };
        double straight = inner - r * t;
        double height = inner + innerSharp * Math.Cos(theta);
        if (straight < -1e-9 || height <= 1e-9) return null;
        return new EdgeFlangeSection(angleDeg, r, th, outerSharp, (r + th) * t - outerSharp, inner, inner + th * t, Math.Max(0, straight), height,
            angleDeg <= 90 + 1e-9 ? inner * Math.Sin(theta) : (double?)null);
    }

    /// <summary>
    /// The four corners of an edge flange's profile, in the part's model space: the edge from <paramref name="start"/>
    /// to <paramref name="end"/>, pulled in <paramref name="offsetMm"/> at both ends, and the same line moved
    /// <paramref name="heightMm"/> along the flange's direction: cos θ along <paramref name="outward"/> (away from the
    /// material, in the face's plane) plus sin θ along <paramref name="normal"/> (the face's outward normal). In order
    /// round the rectangle.
    /// </summary>
    public static IReadOnlyList<Point3> Profile(Point3 start, Point3 end, Vector3 normal, Vector3 outward, double angleDeg, double heightMm, double offsetMm)
    {
        var e = Unit(new Vector3(end.X - start.X, end.Y - start.Y, end.Z - start.Z));
        double theta = Rad(angleDeg);
        var d = new Vector3(Math.Cos(theta) * outward.X + Math.Sin(theta) * normal.X, Math.Cos(theta) * outward.Y + Math.Sin(theta) * normal.Y, Math.Cos(theta) * outward.Z + Math.Sin(theta) * normal.Z);
        var a = Along(start, e, offsetMm);
        var b = Along(end, e, -offsetMm);
        return new[] { a, b, Along(b, d, heightMm), Along(a, d, heightMm) };
    }

    /// <summary>A straight edge's two ends from its description: its centre ± half its length along its direction.</summary>
    public static (Point3 Start, Point3 End) Ends(Point3 middle, Vector3 direction, double lengthMm)
    {
        var u = Unit(direction);
        return (Along(middle, u, -lengthMm / 2), Along(middle, u, lengthMm / 2));
    }

    /// <summary>The shortest distance from any of <paramref name="points"/> to either end of an edge, mm.</summary>
    public static double NearestEndMm(IEnumerable<Point3> points, Point3 start, Point3 end) =>
        points.SelectMany(p => new[] { Distance(p, start), Distance(p, end) }).DefaultIfEmpty(double.PositiveInfinity).Min();

    /// <summary>
    /// What a hem adds, mm³, and how far the measured value may be from it. The fold is a bend of the hem's angle (180°
    /// for closed and open, at half the gap; the angle asked for teardrop and rolled, at the radius asked), whose material
    /// is the bend arithmetic's; closed and open add their straight return, length_mm less the fold; material_inside
    /// takes the fold's outside back from the sheet. Where SOLIDWORKS measures a hem's length from is not yet measured, so
    /// the band is one fold's outside radius of sheet across the hem's width either way.
    /// </summary>
    public static HemMaterial Hem(HemKind kind, double thicknessMm, double widthMm, double? lengthMm, double? gapMm, double? radiusMm, double? angleDeg, BendPosition position)
    {
        double th = thicknessMm;
        bool returns = kind == HemKind.Closed || kind == HemKind.Open;
        double r = returns ? (kind == HemKind.Open ? (gapMm ?? 0) / 2 : 0) : radiusMm ?? 0;
        double angle = returns ? 180 : angleDeg ?? 0;
        double fold = BendArithmetic.FoldedVolumeMm3(1, angle, r, th);
        double straight = returns ? Math.Max(0, (lengthMm ?? 0) - (r + th)) : 0;
        double trim = position == BendPosition.MaterialInside ? r + th : 0;
        double perWidth = th * (straight - trim) + fold;
        return new HemMaterial(widthMm * perWidth, widthMm * th * (r + th));
    }

    static double Distance(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    static Point3 Along(Point3 p, Vector3 v, double d) => new Point3(p.X + v.X * d, p.Y + v.Y * d, p.Z + v.Z * d);

    static Vector3 Unit(Vector3 v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l == 0 ? v : new Vector3(v.X / l, v.Y / l, v.Z / l);
    }
}

/// <summary>An edge flange's cross-section (<see cref="FlangeGeometry.EdgeFlange"/>). Millimetres and degrees.</summary>
public sealed class EdgeFlangeSection
{
    public EdgeFlangeSection(double angleDeg, double radiusMm, double thicknessMm, double outerSharpMm, double trimMm, double innerLengthMm, double outerLengthMm, double straightMm, double heightMm, double? riseMm)
    {
        AngleDeg = angleDeg;
        RadiusMm = radiusMm;
        ThicknessMm = thicknessMm;
        OuterSharpMm = outerSharpMm;
        TrimMm = trimMm;
        InnerLengthMm = innerLengthMm;
        OuterLengthMm = outerLengthMm;
        StraightMm = straightMm;
        HeightMm = heightMm;
        RiseMm = riseMm;
    }

    public double AngleDeg { get; }
    public double RadiusMm { get; }
    public double ThicknessMm { get; }

    /// <summary>How far past the edge, in the face's plane, the outer virtual sharp is.</summary>
    public double OuterSharpMm { get; }

    /// <summary>How much of the sheet behind the edge the bend takes back (its straight part now ends this far short of the edge).</summary>
    public double TrimMm { get; }

    /// <summary>The flange's length from the inner virtual sharp to its end.</summary>
    public double InnerLengthMm { get; }

    /// <summary>The flange's length from the outer virtual sharp to its end.</summary>
    public double OuterLengthMm { get; }

    /// <summary>The flange's straight part, from where the bend ends to its end.</summary>
    public double StraightMm { get; }

    /// <summary>How far the profile runs from the edge to the flange's end, in the flange's direction.</summary>
    public double HeightMm { get; }

    /// <summary>How far the flange's end rises above the face the edge bounds, along its normal; null past 90°, where the bend may stand higher.</summary>
    public double? RiseMm { get; }

    /// <summary>What the flange adds per mm of width, mm²: its straight part and its bend, less the sheet it takes back.</summary>
    public double AddedPerWidthMm2 => ThicknessMm * (StraightMm - TrimMm) + BendArithmetic.FoldedVolumeMm3(1, AngleDeg, RadiusMm, ThicknessMm);

    /// <summary>How much longer the flat pattern is across the edge: the bend's allowance and the straight part, less the sheet taken back.</summary>
    public double FlatChangeMm(BendAllowance allowance) => BendArithmetic.AllowanceMm(AngleDeg, RadiusMm, ThicknessMm, allowance) + StraightMm - TrimMm;
}

/// <summary>What a hem adds (<see cref="FlangeGeometry.Hem"/>), mm³, and the band the measured value must fall within.</summary>
public sealed class HemMaterial
{
    public HemMaterial(double expectedMm3, double toleranceMm3)
    {
        ExpectedMm3 = expectedMm3;
        ToleranceMm3 = toleranceMm3;
    }

    public double ExpectedMm3 { get; }
    public double ToleranceMm3 { get; }
}
