using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>What was drawn: each id's entities, in their named order (a rectangle's bottom, right, top, left; a polygon's side1 …), and each dimension's full name.</summary>
public sealed class BuiltSketch
{
    public Dictionary<string, IReadOnlyList<object>> Drawn { get; } = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids in the order they were drawn.</summary>
    public List<string> Order { get; } = new List<string>();

    public List<string> Dimensions { get; } = new List<string>();
}

/// <summary>
/// Draws a checked sketch request into an open sketch (design 371, Sketches): every entity, then every relation, then
/// every dimension, in the request's order. A relation or dimension SOLIDWORKS does not add is operation_failed naming
/// its field, and the operation rolls the sketch back. References to the part's edges and vertices are resolved by the
/// caller before anything is drawn.
/// </summary>
public static class SketchBuilder
{
    static readonly string[] RectangleSides = { "bottom", "right", "top", "left" };

    public static BuiltSketch Draw(ISketchEditor editor, SketchDraft draft, Func<DraftTarget, object> external)
    {
        var built = new BuiltSketch();
        foreach (var e in draft.Entities)
        {
            built.Drawn[e.Id] = DrawOne(editor, e);
            built.Order.Add(e.Id);
        }
        foreach (var r in draft.Relations)
        {
            var targets = r.Targets.Select(t => Target(editor, built, t, external)).ToList();
            if (!editor.Relate(r.Type, targets))
            {
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"{r.Field}: SOLIDWORKS did not add the {WireNames.ForEnum(r.Type)} relation between {string.Join(", ", r.Targets.Select(t => t.Text))}; check that they can be related that way",
                    new JsonObject { ["field"] = r.Field });
            }
        }
        foreach (var d in draft.Dimensions)
        {
            var targets = d.Targets.Select(t => Target(editor, built, t, external)).ToList();
            string? name = editor.Dimension(d.Kind, targets, d.Value, d.Name);
            if (name == null)
            {
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"{d.Field}: SOLIDWORKS did not add a {WireNames.ForEnum(d.Kind)} dimension to {string.Join(", ", d.Targets.Select(t => t.Text))}",
                    new JsonObject { ["field"] = d.Field });
            }
            built.Dimensions.Add(name);
        }
        return built;
    }

    static IReadOnlyList<object> DrawOne(ISketchEditor editor, DraftEntity e)
    {
        switch (e.Item)
        {
            case SketchLine l:
                return new[] { editor.Line(l.Start, l.End, l.Construction == true) };
            case SketchCenterLine c:
                return new[] { editor.Line(c.Start, c.End, construction: true) };
            case SketchArc a:
                return new[] { editor.Arc(a.Center, a.Start, a.End, a.Direction != ArcDirection.Clockwise) };
            case SketchCircle c:
                return new[] { editor.Circle(c.Center, c.RadiusMm) };
            case SketchRectangle r:
            {
                var corner1 = r.Centered == true ? new Point2(2 * r.Corner1.X - r.Corner2.X, 2 * r.Corner1.Y - r.Corner2.Y) : r.Corner1;
                return RectangleOrder(editor, editor.Rectangle(corner1, r.Corner2));
            }
            case SketchPolygon p:
                return PolygonOrder(editor, editor.Polygon(p.Center, p.Vertex, p.Sides), p);
            case SketchSlot s:
                return editor.Slot(s.Start, s.End, s.WidthMm);
            case SketchSpline s:
                return new[] { editor.Spline(s.Points) };
            case SketchPointItem p:
                return new[] { editor.Point(p.At) };
            default:
                throw new InvalidOperationException($"cannot draw a {e.Item.GetType().Name}");
        }
    }

    /// <summary>A rectangle's lines as bottom, right, top, left.</summary>
    static IReadOnlyList<object> RectangleOrder(ISketchEditor editor, IReadOnlyList<object> lines)
    {
        if (lines.Count != 4) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS drew a rectangle with {lines.Count} lines");
        var measured = lines.Select(l => (Line: l, Ends: editor.Ends(l))).ToList();
        bool Horizontal((object Line, (Point2 Start, Point2 End) Ends) m) => Math.Abs(m.Ends.End.Y - m.Ends.Start.Y) < Math.Abs(m.Ends.End.X - m.Ends.Start.X);
        double MidX((object Line, (Point2 Start, Point2 End) Ends) m) => (m.Ends.Start.X + m.Ends.End.X) / 2;
        double MidY((object Line, (Point2 Start, Point2 End) Ends) m) => (m.Ends.Start.Y + m.Ends.End.Y) / 2;
        var horizontal = measured.Where(Horizontal).OrderBy(MidY).ToList();
        var vertical = measured.Where(m => !Horizontal(m)).OrderBy(MidX).ToList();
        if (horizontal.Count != 2 || vertical.Count != 2) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS drew a rectangle whose sides are not along the sketch's axes");
        return new[] { horizontal[0].Line, vertical[1].Line, horizontal[1].Line, vertical[0].Line };
    }

    /// <summary>A polygon's sides counterclockwise, side1 the first after the vertex.</summary>
    static IReadOnlyList<object> PolygonOrder(ISketchEditor editor, IReadOnlyList<object> sides, SketchPolygon p)
    {
        if (sides.Count != p.Sides) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS drew a polygon with {sides.Count} sides, not {p.Sides}");
        double start = Math.Atan2(p.Vertex.Y - p.Center.Y, p.Vertex.X - p.Center.X);
        double Angle(object side)
        {
            var (a, b) = editor.Ends(side);
            double angle = Math.Atan2((a.Y + b.Y) / 2 - p.Center.Y, (a.X + b.X) / 2 - p.Center.X) - start;
            while (angle < 0) angle += 2 * Math.PI;
            return angle;
        }
        return sides.OrderBy(Angle).ToList();
    }

    static object Target(ISketchEditor editor, BuiltSketch built, DraftTarget t, Func<DraftTarget, object> external)
    {
        if (t.IsOrigin) return SketchOrigin.Instance;
        if (t.IsReference) return external(t);
        var drawn = built.Drawn[t.Id!];
        switch (t.Part)
        {
            case null:
                return drawn[0];
            case "start":
            case "end":
            case "center":
                return editor.PointOf(drawn[0], t.Part);
            default:
                int side = Array.IndexOf(RectangleSides, t.Part);
                if (side < 0) side = int.Parse(t.Part.Substring("side".Length), System.Globalization.CultureInfo.InvariantCulture) - 1;
                return drawn[side];
        }
    }
}
