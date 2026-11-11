using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>What model.find was asked for, already validated.</summary>
public sealed class EntityQuery
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 1000;
    public const double DefaultAngleToleranceDeg = 1.0;
    public const double DefaultMaxDistanceMm = 1.0;

    public string? Feature { get; set; }
    public GeometryType? Geometry { get; set; }
    public Vector3? Direction { get; set; }
    public double AngleToleranceDeg { get; set; } = DefaultAngleToleranceDeg;
    public Point3? Near { get; set; }
    public double MaxDistanceMm { get; set; } = DefaultMaxDistanceMm;
    public int Limit { get; set; } = DefaultLimit;
}

/// <summary>
/// The filtering behind model.find (design 371, References). Candidates come from SOLIDWORKS with their description
/// (and, when the query is near a point, their distance from it); this keeps what matches every filter given.
/// </summary>
public static class EntityFilter
{
    /// <summary>The matching entities, nearest first when the query is near a point, else in SOLIDWORKS' order; at most Limit, with the total.</summary>
    public static (IReadOnlyList<T> Kept, int Total) Apply<T>(IEnumerable<T> candidates, Func<T, EntityInfo> info, EntityQuery query)
    {
        var matching = candidates.Where(c => Matches(info(c), query)).ToList();
        if (query.Near != null) matching = matching.OrderBy(c => info(c).DistanceMm ?? double.MaxValue).ToList();
        return (matching.Take(query.Limit).ToList(), matching.Count);
    }

    public static bool Matches(EntityInfo e, EntityQuery q)
    {
        if (q.Feature != null && !MadeBy(e, q.Feature)) return false;
        if (q.Geometry != null && e.Geometry != q.Geometry) return false;
        if (q.Direction != null && !Along(e, q.Direction, q.AngleToleranceDeg)) return false;
        if (q.Near != null && (e.DistanceMm == null || e.DistanceMm > q.MaxDistanceMm)) return false;
        return true;
    }

    /// <summary>True when <paramref name="feature"/> made the entity, or (for an edge or vertex) one of the faces it lies on.</summary>
    public static bool MadeBy(EntityInfo e, string feature) =>
        string.Equals(e.Feature, feature, StringComparison.OrdinalIgnoreCase)
        || e.Features != null && e.Features.Any(f => string.Equals(f, feature, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A planar face (or plane) whose normal points the same way as <paramref name="direction"/>; an edge, cylinder,
    /// cone or axis whose axis is parallel to it either way. Anything without a direction never matches.
    /// </summary>
    static bool Along(EntityInfo e, Vector3 direction, double toleranceDeg)
    {
        if (e.Normal != null) return AngleDeg(e.Normal, direction) <= toleranceDeg;
        if (e.Axis != null)
        {
            double angle = AngleDeg(e.Axis, direction);
            return angle <= toleranceDeg || 180 - angle <= toleranceDeg;
        }
        return false;
    }

    public static double AngleDeg(Vector3 a, Vector3 b)
    {
        double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
        if (la == 0 || lb == 0) return 180;
        double cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
        return Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180 / Math.PI;
    }

    /// <summary>
    /// The description a reference carries: what the entity was when it was issued, as reference_unresolved reports it,
    /// for example "plane face of Boss-Extrude1, 800 mm²" or "line edge of Cut-Extrude1, 20 mm"; in an assembly, with the
    /// component it belongs to: "plane face of Boss-Extrude1 in bracket-1, 800 mm²".
    /// </summary>
    public static string Describe(EntityInfo e)
    {
        string geometry = WireNames.ForEnum(e.Geometry);
        string kind = WireNames.ForEnum(e.Kind).Replace('_', ' ');
        string text = e.Name != null ? $"{kind} {e.Name}" : $"{geometry} {kind}";
        if (e.Feature != null) text += $" of {e.Feature}";
        if (e.Component != null && e.Kind != ReferenceKind.Component) text += $" in {e.Component}";
        var c = CultureInfo.InvariantCulture;
        if (e.AreaMm2 != null) text += string.Format(c, ", {0:0.###} mm²", e.AreaMm2);
        if (e.LengthMm != null) text += string.Format(c, ", {0:0.###} mm", e.LengthMm);
        if (e.RadiusMm != null) text += string.Format(c, ", radius {0:0.###} mm", e.RadiusMm);
        if (e.Kind == ReferenceKind.Vertex && e.Origin != null) text += " at " + e.Origin;
        return text;
    }
}
