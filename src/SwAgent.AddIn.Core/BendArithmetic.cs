using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The bend arithmetic every exact sheet-metal check uses (design 454, The bend arithmetic). A bend of angle θ, inner
/// radius R, thickness T and width w holds w·θ/2·((R+T)² − R²) of material folded, and develops when flattened to its
/// bend allowance BA = θ·(R + K·T) (or the allowance given, or 2·(R+T)·tan(θ/2) − deduction). Millimetres and degrees.
/// </summary>
public static class BendArithmetic
{
    static double Rad(double deg) => deg * Math.PI / 180;

    /// <summary>The length a bend develops to when flattened, mm.</summary>
    public static double AllowanceMm(double angleDeg, double radiusMm, double thicknessMm, BendAllowance allowance)
    {
        if (allowance.KFactor != null) return Rad(angleDeg) * (radiusMm + allowance.KFactor.Value * thicknessMm);
        if (allowance.AllowanceMm != null) return allowance.AllowanceMm.Value;
        if (allowance.DeductionMm != null) return 2 * OutsideSetbackMm(angleDeg, radiusMm, thicknessMm) - allowance.DeductionMm.Value;
        throw new ArgumentException("an allowance needs k_factor, allowance_mm or deduction_mm", nameof(allowance));
    }

    /// <summary>(R+T)·tan(θ/2): how far the bend's tangent line is from the outer virtual sharp, mm.</summary>
    public static double OutsideSetbackMm(double angleDeg, double radiusMm, double thicknessMm) => (radiusMm + thicknessMm) * Math.Tan(Rad(angleDeg) / 2);

    /// <summary>The bend deduction the allowance amounts to: 2·(R+T)·tan(θ/2) − BA, mm.</summary>
    public static double DeductionMm(double angleDeg, double radiusMm, double thicknessMm, BendAllowance allowance) =>
        2 * OutsideSetbackMm(angleDeg, radiusMm, thicknessMm) - AllowanceMm(angleDeg, radiusMm, thicknessMm, allowance);

    /// <summary>The material a bend holds folded: w·θ/2·((R+T)² − R²), mm³.</summary>
    public static double FoldedVolumeMm3(double widthMm, double angleDeg, double radiusMm, double thicknessMm) =>
        widthMm * Rad(angleDeg) / 2 * ((radiusMm + thicknessMm) * (radiusMm + thicknessMm) - radiusMm * radiusMm);

    /// <summary>How much flattening a bend changes the part's volume: w·T·(BA − θ·(R + T/2)), mm³; zero only at K = ½.</summary>
    public static double FlattenChangeMm3(double widthMm, double angleDeg, double radiusMm, double thicknessMm, BendAllowance allowance) =>
        widthMm * thicknessMm * (AllowanceMm(angleDeg, radiusMm, thicknessMm, allowance) - Rad(angleDeg) * (radiusMm + thicknessMm / 2));

    /// <summary>A flat length: the straight legs between the bends plus each bend's allowance, mm.</summary>
    public static double FlatLengthMm(IEnumerable<double> straightLegsMm, IEnumerable<double> allowancesMm) => straightLegsMm.Sum() + allowancesMm.Sum();

    /// <summary>
    /// The points of a single open chain the lines make, in order from one free end to the other; null when they make
    /// a closed loop, a branch, more than one chain, or nothing.
    /// </summary>
    public static IReadOnlyList<Point2>? Chain(IReadOnlyList<(Point2 Start, Point2 End)> lines)
    {
        const double join = 1e-6;
        if (lines.Count == 0) return null;
        bool Same(Point2 a, Point2 b) => Math.Abs(a.X - b.X) <= join && Math.Abs(a.Y - b.Y) <= join;
        int Ends(Point2 p) => lines.Count(l => Same(l.Start, p)) + lines.Count(l => Same(l.End, p));
        var free = lines.SelectMany(l => new[] { l.Start, l.End }).Where(p => Ends(p) == 1).ToList();
        if (free.Count != 2 || lines.SelectMany(l => new[] { l.Start, l.End }).Any(p => Ends(p) > 2)) return null;
        var points = new List<Point2> { free[0] };
        var left = lines.ToList();
        while (left.Count > 0)
        {
            var at = points[points.Count - 1];
            int i = left.FindIndex(l => Same(l.Start, at) || Same(l.End, at));
            if (i < 0) return null;
            points.Add(Same(left[i].Start, at) ? left[i].End : left[i].Start);
            left.RemoveAt(i);
        }
        return points;
    }

    /// <summary>
    /// What a base flange from an open chain of lines holds: each sharp corner becomes a bend of inner radius R, the
    /// lines are one face of a sheet T thick, and it is extruded <paramref name="depthMm"/>. Which face the lines are
    /// depends on the side the material is on, so both sides are worked out: at a corner turning towards the material
    /// the lines are the outer face (the straight legs end (R+T)·tan(φ/2) short of the corner), turning away the inner
    /// face (R·tan(φ/2)). Straight legs are the same length on every layer, so the volume is depth × T × (the legs plus
    /// each bend's middle arc φ·(R + T/2)). A side whose legs would be shorter than nothing is null.
    /// </summary>
    public static OpenProfileVolume OpenProfile(IReadOnlyList<Point2> chain, double radiusMm, double thicknessMm, double depthMm)
    {
        var corners = new List<(double AngleDeg, int Turn)>();
        for (int i = 1; i + 1 < chain.Count; i++)
        {
            double ax = chain[i].X - chain[i - 1].X, ay = chain[i].Y - chain[i - 1].Y;
            double bx = chain[i + 1].X - chain[i].X, by = chain[i + 1].Y - chain[i].Y;
            double cross = ax * by - ay * bx, dot = ax * bx + ay * by;
            double angle = Math.Atan2(Math.Abs(cross), dot) * 180 / Math.PI;
            corners.Add((angle, cross > 0 ? 1 : cross < 0 ? -1 : 0));
        }
        double? Side(int material)
        {
            var legs = new List<double>();
            for (int i = 0; i + 1 < chain.Count; i++)
                legs.Add(Math.Sqrt(Math.Pow(chain[i + 1].X - chain[i].X, 2) + Math.Pow(chain[i + 1].Y - chain[i].Y, 2)));
            double arcs = 0;
            for (int c = 0; c < corners.Count; c++)
            {
                var (angle, turn) = corners[c];
                if (angle < 1e-6) continue;
                if (angle >= 179.999) return null;
                double setback = (turn == material ? radiusMm + thicknessMm : radiusMm) * Math.Tan(Rad(angle) / 2);
                legs[c] -= setback;
                legs[c + 1] -= setback;
                arcs += Rad(angle) * (radiusMm + thicknessMm / 2);
            }
            if (legs.Any(l => l < -1e-9)) return null;
            return depthMm * thicknessMm * (legs.Sum() + arcs);
        }
        return new OpenProfileVolume(corners.Count(c => c.AngleDeg >= 1e-6), corners.Where(c => c.AngleDeg >= 1e-6).Select(c => c.AngleDeg).ToList(), Side(1), Side(-1));
    }
}

/// <summary>What a base flange from an open chain holds, with the material on the chain's left or on its right.</summary>
public sealed class OpenProfileVolume
{
    public OpenProfileVolume(int bends, IReadOnlyList<double> anglesDeg, double? leftMm3, double? rightMm3)
    {
        Bends = bends;
        AnglesDeg = anglesDeg;
        LeftMm3 = leftMm3;
        RightMm3 = rightMm3;
    }

    /// <summary>How many sharp corners, so how many bends.</summary>
    public int Bends { get; }

    /// <summary>Each corner's bend angle (how far the chain turns there), degrees, in order.</summary>
    public IReadOnlyList<double> AnglesDeg { get; }

    /// <summary>The volume with the material on the chain's left, mm³; null when that side cannot be made.</summary>
    public double? LeftMm3 { get; }

    /// <summary>The volume with the material on the chain's right, mm³; null when that side cannot be made.</summary>
    public double? RightMm3 { get; }
}
