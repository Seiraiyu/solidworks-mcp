using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

public enum SketchItemKind
{
    Line,
    CenterLine,
    Arc,
    Circle,
    Rectangle,
    Polygon,
    Slot,
    Spline,
    Point,
}

/// <summary>One entity to draw: its id, kind, the request's item for it, and its JSON pointer.</summary>
public sealed class DraftEntity
{
    public DraftEntity(string id, SketchItemKind kind, object item, string field)
    {
        Id = id;
        Kind = kind;
        Item = item;
        Field = field;
    }

    public string Id { get; }
    public SketchItemKind Kind { get; }

    /// <summary>The request's SketchLine, SketchArc … for it.</summary>
    public object Item { get; }

    public string Field { get; }
}

/// <summary>What a relation or dimension names: an id and an optional part (start, end, center, bottom, side2 …), the origin, or a reference.</summary>
public sealed class DraftTarget
{
    public DraftTarget(string text, string field, string? id = null, string? part = null, bool isOrigin = false, bool isReference = false)
    {
        Text = text;
        Field = field;
        Id = id;
        Part = part;
        IsOrigin = isOrigin;
        IsReference = isReference;
    }

    public string Text { get; }
    public string Field { get; }
    public string? Id { get; }
    public string? Part { get; }
    public bool IsOrigin { get; }
    public bool IsReference { get; }

    /// <summary>True when it names a point: an end or centre, a sketch point, the origin, or a vertex reference.</summary>
    public bool IsPoint(Func<string, SketchItemKind> kindOf) =>
        IsOrigin
        || Part == "start" || Part == "end" || Part == "center"
        || (Id != null && Part == null && kindOf(Id) == SketchItemKind.Point)
        || (IsReference && (References.KindOf(Text) == ReferenceKind.Vertex || References.KindOf(Text) == ReferenceKind.SketchPoint));
}

public sealed class DraftRelation
{
    public DraftRelation(SketchRelationType type, IReadOnlyList<DraftTarget> targets, string field)
    {
        Type = type;
        Targets = targets;
        Field = field;
    }

    public SketchRelationType Type { get; }
    public IReadOnlyList<DraftTarget> Targets { get; }
    public string Field { get; }
}

public sealed class DraftDimension
{
    public DraftDimension(string? name, DimensionKind kind, double value, IReadOnlyList<DraftTarget> targets, string field)
    {
        Name = name;
        Kind = kind;
        Value = value;
        Targets = targets;
        Field = field;
    }

    public string? Name { get; }
    public DimensionKind Kind { get; }

    /// <summary>mm, or degrees for an angle.</summary>
    public double Value { get; }

    public IReadOnlyList<DraftTarget> Targets { get; }
    public string Field { get; }
}

/// <summary>How many segments of each kind SOLIDWORKS should hold for what was drawn (construction lines at least this many: polygons and slots add their own).</summary>
public sealed class SketchCounts
{
    public int Lines { get; set; }
    public int ConstructionLines { get; set; }
    public int Arcs { get; set; }
    public int Circles { get; set; }
    public int Splines { get; set; }
}

/// <summary>
/// A sketch request, checked before anything is drawn (design 371, Sketches): every id is a short name unique in the
/// request, every coordinate is finite and within reach, every shape is drawable, and every relation and dimension
/// names entities that exist with parts they have. A problem is invalid_argument with details.field, a JSON pointer
/// into the request (/lines/2/end). Pure, so the whole of it is unit-tested.
/// </summary>
public sealed class SketchDraft
{
    /// <summary>The farthest a sketch coordinate may be from the sketch origin, mm.</summary>
    public const double Reach = 100_000;

    const double Tiny = 1e-6;
    static readonly Regex IdPattern = new Regex("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant);
    static readonly string[] RectangleSides = { "bottom", "right", "top", "left" };

    SketchDraft()
    {
    }

    public List<DraftEntity> Entities { get; } = new List<DraftEntity>();
    public List<DraftRelation> Relations { get; } = new List<DraftRelation>();
    public List<DraftDimension> Dimensions { get; } = new List<DraftDimension>();
    public SketchCounts Expected { get; } = new SketchCounts();

    public SketchItemKind KindOf(string id) => Entities.First(e => e.Id == id).Kind;

    /// <param name="prefix">The pointer to the geometry in the request: "" for sketch.create, "/add" for sketch.modify.</param>
    public static SketchDraft Check(SketchGeometry g, string prefix)
    {
        var d = new SketchDraft();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, SketchItemKind kind, object item, string field)
        {
            if (!IdPattern.IsMatch(id ?? ""))
                throw Invalid(field + "/id", $"'{id}' is not an id: use a letter, then up to 31 letters, digits or _");
            if (string.Equals(id, "r1", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "origin", StringComparison.OrdinalIgnoreCase))
                throw Invalid(field + "/id", $"'{id}' is reserved (references start r1., and origin names the sketch's origin); choose another id");
            if (!ids.Add(id!)) throw Invalid(field + "/id", $"the id '{id}' is used twice; ids must be unique in the request");
            d.Entities.Add(new DraftEntity(id!, kind, item, field));
        }

        Each(g.Lines, prefix + "/lines", (l, f) =>
        {
            Add(l.Id, SketchItemKind.Line, l, f);
            Point(l.Start, f + "/start");
            Point(l.End, f + "/end");
            Distinct(l.Start, l.End, f + "/end", "a line's end must differ from its start");
            if (l.Construction == true) d.Expected.ConstructionLines++;
            else d.Expected.Lines++;
        });
        Each(g.CenterLines, prefix + "/center_lines", (l, f) =>
        {
            Add(l.Id, SketchItemKind.CenterLine, l, f);
            Point(l.Start, f + "/start");
            Point(l.End, f + "/end");
            Distinct(l.Start, l.End, f + "/end", "a centerline's end must differ from its start");
            d.Expected.ConstructionLines++;
        });
        Each(g.Arcs, prefix + "/arcs", (a, f) =>
        {
            Add(a.Id, SketchItemKind.Arc, a, f);
            Point(a.Center, f + "/center");
            Point(a.Start, f + "/start");
            Point(a.End, f + "/end");
            double r1 = Distance(a.Center, a.Start), r2 = Distance(a.Center, a.End);
            if (r1 < Tiny) throw Invalid(f + "/start", "an arc's start must differ from its centre");
            if (Math.Abs(r1 - r2) > 1e-3) throw Invalid(f + "/end", $"an arc's end must be as far from its centre as its start ({r1:0.###} mm), not {r2:0.###} mm");
            Distinct(a.Start, a.End, f + "/end", "an arc's end must differ from its start (draw a circle instead)");
            d.Expected.Arcs++;
        });
        Each(g.Circles, prefix + "/circles", (c, f) =>
        {
            Add(c.Id, SketchItemKind.Circle, c, f);
            Point(c.Center, f + "/center");
            Positive(c.RadiusMm, f + "/radius_mm", "a circle's radius");
            d.Expected.Circles++;
        });
        Each(g.Rectangles, prefix + "/rectangles", (r, f) =>
        {
            Add(r.Id, SketchItemKind.Rectangle, r, f);
            Point(r.Corner1, f + "/corner1");
            Point(r.Corner2, f + "/corner2");
            if (Math.Abs(r.Corner1.X - r.Corner2.X) < Tiny || Math.Abs(r.Corner1.Y - r.Corner2.Y) < Tiny)
                throw Invalid(f + "/corner2", "a rectangle's corners must differ in both x and y");
            d.Expected.Lines += 4;
        });
        Each(g.Polygons, prefix + "/polygons", (p, f) =>
        {
            Add(p.Id, SketchItemKind.Polygon, p, f);
            Point(p.Center, f + "/center");
            Point(p.Vertex, f + "/vertex");
            Distinct(p.Center, p.Vertex, f + "/vertex", "a polygon's vertex must differ from its centre");
            if (p.Sides < 3 || p.Sides > 64) throw Invalid(f + "/sides", "a polygon has 3 to 64 sides");
            d.Expected.Lines += p.Sides;
        });
        Each(g.Slots, prefix + "/slots", (s, f) =>
        {
            Add(s.Id, SketchItemKind.Slot, s, f);
            Point(s.Start, f + "/start");
            Point(s.End, f + "/end");
            Distinct(s.Start, s.End, f + "/end", "a slot's end must differ from its start");
            Positive(s.WidthMm, f + "/width_mm", "a slot's width");
            d.Expected.Lines += 2;
            d.Expected.Arcs += 2;
        });
        Each(g.Splines, prefix + "/splines", (s, f) =>
        {
            Add(s.Id, SketchItemKind.Spline, s, f);
            if (s.Points == null || s.Points.Count < 2) throw Invalid(f + "/points", "a spline needs at least 2 points");
            for (int i = 0; i < s.Points.Count; i++)
            {
                Point(s.Points[i], $"{f}/points/{i}");
                if (i > 0) Distinct(s.Points[i - 1], s.Points[i], $"{f}/points/{i}", "a spline's points must differ from the one before");
            }
            d.Expected.Splines++;
        });
        Each(g.Points, prefix + "/points", (p, f) =>
        {
            Add(p.Id, SketchItemKind.Point, p, f);
            Point(p.At, f + "/at");
        });

        Each(g.Relations, prefix + "/relations", (r, f) =>
        {
            var targets = d.Targets(r.Entities, f + "/entities");
            var (min, max) = Arity(r.Type);
            if (targets.Count < min || targets.Count > max)
                throw Invalid(f + "/entities", $"a {WireNames.ForEnum(r.Type)} relation relates {(min == max ? $"{min}" : max == int.MaxValue ? $"{min} or more" : $"{min} to {max}")} entities, not {targets.Count}");
            d.Relations.Add(new DraftRelation(r.Type, targets, f));
        });

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Each(g.Dimensions, prefix + "/dimensions", (m, f) =>
        {
            if (m.Name != null)
            {
                if (!IdPattern.IsMatch(m.Name)) throw Invalid(f + "/name", $"'{m.Name}' is not a dimension name: use a letter, then up to 31 letters, digits or _");
                if (!names.Add(m.Name)) throw Invalid(f + "/name", $"the dimension name '{m.Name}' is used twice");
            }
            var targets = d.Targets(m.Entities, f + "/entities");
            if (targets.Count < 1 || targets.Count > 2) throw Invalid(f + "/entities", "a dimension measures one entity or the distance or angle between two");
            var kind = m.Kind ?? d.DefaultKind(targets);
            d.CheckKind(kind, targets, f + "/kind");
            if (double.IsNaN(m.Value) || double.IsInfinity(m.Value) || m.Value <= 0 || (kind == DimensionKind.Angle ? m.Value >= 360 : m.Value > Reach))
                throw Invalid(f + "/value", kind == DimensionKind.Angle ? "an angle is more than 0 and less than 360 degrees" : $"a dimension is more than 0 and at most {Reach} mm");
            d.Dimensions.Add(new DraftDimension(m.Name, kind, m.Value, targets, f));
        });

        if (d.Entities.Count == 0 && d.Relations.Count == 0 && d.Dimensions.Count == 0)
            throw Invalid(prefix, "the sketch has nothing in it: give at least one entity");
        return d;
    }

    List<DraftTarget> Targets(List<string>? texts, string field)
    {
        var targets = new List<DraftTarget>();
        if (texts == null) return targets;
        for (int i = 0; i < texts.Count; i++)
        {
            string text = texts[i] ?? "";
            string f = $"{field}/{i}";
            if (References.IsReference(text))
            {
                var kind = References.KindOf(text);
                if (kind != ReferenceKind.Edge && kind != ReferenceKind.Vertex && kind != ReferenceKind.SketchEntity && kind != ReferenceKind.SketchPoint)
                    throw Invalid(f, "a relation or dimension can name the part's edges and vertices, or sketch entities and points, by reference; this reference is not one");
                targets.Add(new DraftTarget(text, f, isReference: true));
                continue;
            }
            if (string.Equals(text, "origin", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(new DraftTarget(text, f, isOrigin: true));
                continue;
            }
            int dot = text.IndexOf('.');
            string id = dot < 0 ? text : text.Substring(0, dot);
            string? part = dot < 0 ? null : text.Substring(dot + 1);
            var entity = Entities.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(f, $"'{text}' names no entity in this request (ids: {string.Join(", ", Entities.Select(e => e.Id))}), and is not a reference or origin");
            string? why = PartProblem(entity, part);
            if (why != null) throw Invalid(f, $"'{text}': {why}");
            targets.Add(new DraftTarget(text, f, entity.Id, part));
        }
        return targets;
    }

    static string? PartProblem(DraftEntity e, string? part)
    {
        string[] parts = e.Kind switch
        {
            SketchItemKind.Line => new[] { "start", "end" },
            SketchItemKind.CenterLine => new[] { "start", "end" },
            SketchItemKind.Arc => new[] { "start", "end", "center" },
            SketchItemKind.Circle => new[] { "center" },
            SketchItemKind.Rectangle => RectangleSides,
            SketchItemKind.Polygon => Enumerable.Range(1, ((SketchPolygon)e.Item).Sides).Select(i => "side" + i).ToArray(),
            _ => new string[0],
        };
        bool wholeAllowed = e.Kind != SketchItemKind.Rectangle && e.Kind != SketchItemKind.Polygon && e.Kind != SketchItemKind.Slot;
        if (part == null) return wholeAllowed ? null : $"a {WireNames.ForEnum(e.Kind)} is not one entity; name one of its parts: {string.Join(", ", parts.Select(p => e.Id + "." + p))}";
        if (parts.Contains(part, StringComparer.Ordinal)) return null;
        return parts.Length == 0 ? $"a {WireNames.ForEnum(e.Kind)} has no parts to name" : $"a {WireNames.ForEnum(e.Kind)} has the parts {string.Join(", ", parts)}, not '{part}'";
    }

    DimensionKind DefaultKind(IReadOnlyList<DraftTarget> targets)
    {
        if (targets.Count == 1 && targets[0].Id != null && targets[0].Part == null)
        {
            var kind = KindOf(targets[0].Id!);
            if (kind == SketchItemKind.Circle) return DimensionKind.Diameter;
            if (kind == SketchItemKind.Arc) return DimensionKind.Radius;
        }
        return DimensionKind.Linear;
    }

    void CheckKind(DimensionKind kind, IReadOnlyList<DraftTarget> targets, string field)
    {
        bool oneRound = targets.Count == 1 && targets[0].Id != null && targets[0].Part == null
            && (KindOf(targets[0].Id!) == SketchItemKind.Circle || KindOf(targets[0].Id!) == SketchItemKind.Arc);
        switch (kind)
        {
            case DimensionKind.Radius:
            case DimensionKind.Diameter:
                if (!oneRound) throw Invalid(field, $"a {WireNames.ForEnum(kind)} dimension measures one circle or arc");
                break;
            case DimensionKind.Angle:
                if (targets.Count != 2 || targets.Any(t => t.IsPoint(KindOf))) throw Invalid(field, "an angle dimension measures between two lines");
                break;
            default:
                if (targets.Count == 1 && targets[0].IsPoint(KindOf)) throw Invalid(field, "a point has no length: dimension it to something else");
                break;
        }
    }

    static (int Min, int Max) Arity(SketchRelationType type) => type switch
    {
        SketchRelationType.Horizontal => (1, 2),
        SketchRelationType.Vertical => (1, 2),
        SketchRelationType.Coincident => (2, 2),
        SketchRelationType.Tangent => (2, 2),
        SketchRelationType.Concentric => (2, 2),
        SketchRelationType.Perpendicular => (2, 2),
        SketchRelationType.Midpoint => (2, 2),
        SketchRelationType.Symmetric => (3, 3),
        SketchRelationType.Fix => (1, int.MaxValue),
        _ => (2, int.MaxValue),
    };

    static void Each<T>(List<T>? items, string field, Action<T, string> check)
    {
        if (items == null) return;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] == null) throw Invalid($"{field}/{i}", "an entry is null");
            check(items[i], $"{field}/{i}");
        }
    }

    static void Point(Point2? p, string field)
    {
        if (p == null) throw Invalid(field, "a point is required");
        if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
            throw Invalid(field, "a point's coordinates must be numbers");
        if (Math.Abs(p.X) > Reach || Math.Abs(p.Y) > Reach)
            throw Invalid(field, $"a point must be within {Reach} mm of the sketch origin");
    }

    static void Distinct(Point2 a, Point2 b, string field, string why)
    {
        if (Distance(a, b) < Tiny) throw Invalid(field, why);
    }

    static void Positive(double v, string field, string what)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0 || v > Reach) throw Invalid(field, $"{what} must be more than 0 and at most {Reach} mm");
    }

    static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, $"{(field.Length == 0 ? "the request" : field)}: {message}", new JsonObject { ["field"] = field });
}
