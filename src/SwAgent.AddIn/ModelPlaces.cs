using System;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>A <see cref="ModelPlace"/> resolved: the point in the view's model, model mm, and what it is, for results and messages.</summary>
public sealed class ResolvedPlace
{
    public ResolvedPlace(Point3 point, string described)
    {
        Point = point;
        Described = described;
    }

    public Point3 Point { get; }

    public string Described { get; }
}

/// <summary>
/// Resolves a <see cref="ModelPlace"/> (design 611, Sections and details: a section's line passes through a model point
/// or an entity; a detail is centred on one) in a view's model: exactly one of a point, a reference or a name, the entity
/// resolved with phase 4's resolver (never substituted) and its position taken as <see cref="PointOf"/> says. Each
/// mistake is invalid_argument naming its JSON pointer.
/// </summary>
public static class ModelPlaces
{
    /// <summary>The kinds whose position a place takes.</summary>
    static readonly ReferenceKind[] Kinds = { ReferenceKind.Vertex, ReferenceKind.Edge, ReferenceKind.Face, ReferenceKind.Axis, ReferenceKind.Point };

    public static ResolvedPlace Resolve(ReferenceService references, ISwDocument model, ModelPlace given, string field)
    {
        int forms = (given.Point != null ? 1 : 0) + (given.Reference != null ? 1 : 0) + (given.Entity != null ? 1 : 0);
        if (forms == 0) throw Invalid(field + "/point", $"{field} names no place: give point (model mm), reference (issued in the view's model) or entity (a name, or with component an entity of that component)");
        if (forms > 1) throw Invalid(field, $"{field} gives {forms} of point, reference and entity; give one");
        if (given.Component != null && given.Entity == null)
            throw Invalid(field + "/component", $"{field}/component goes with entity (an entity of that component), not with point or reference");
        if (given.Point is Point3 p)
        {
            if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)) throw Invalid(field + "/point", $"{field}/point must be three finite numbers, model mm");
            return new ResolvedPlace(p, $"the point {p}");
        }
        string sub = given.Reference != null ? "/reference" : "/entity";
        var (entity, _) = given.Reference != null
            ? references.Resolve(model, given.Reference, field + sub, Kinds)
            : given.Component != null
                ? references.ResolveInComponent(model, given.Entity!, given.Component, field + sub, field + "/component", Kinds)
                : references.Resolve(model, given.Entity!, field + sub, Kinds);
        var info = references.Describe(model, entity);
        var point = PointOf(info);
        if (point == null)
        {
            throw Invalid(field + sub,
                $"{field}{sub} names a {WireNames.ForEnum(info.Geometry)} {WireNames.ForEnum(info.Kind)}, which has no one point to take; name a vertex, a straight or circular edge, an axis, a point or a cylindrical face");
        }
        string what = info.Name ?? $"the {WireNames.ForEnum(info.Geometry)} {WireNames.ForEnum(info.Kind)}";
        return new ResolvedPlace(point, $"{what}{(info.Component != null ? " of " + info.Component : "")} at {point}");
    }

    /// <summary>
    /// The point an entity gives, model mm: a vertex or a point itself; a straight edge's midpoint; a circular edge's
    /// centre; a point on an axis or on a cylindrical face's axis (each EntityInfo.Origin as phase 3 describes it). Null
    /// for anything else (a planar face, a spline, an entity described without a position).
    /// </summary>
    public static Point3? PointOf(EntityInfo info)
    {
        if (info.Origin == null) return null;
        switch (info.Kind)
        {
            case ReferenceKind.Vertex:
            case ReferenceKind.Point:
            case ReferenceKind.Axis:
                return info.Origin;
            case ReferenceKind.Edge:
                return info.Geometry == GeometryType.Line || info.Geometry == GeometryType.Circle ? info.Origin : null;
            case ReferenceKind.Face:
                return info.Geometry == GeometryType.Cylinder ? info.Origin : null;
            default:
                return null;
        }
    }

    static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });
}
