using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// What a reference dimension should read, measured from the model: <see cref="Value"/> in model mm (degrees for an
/// angle), and for an angle its supplement, since which of the two SOLIDWORKS dimensions depends on where its text goes.
/// </summary>
public sealed class DimensionExpectation
{
    public DimensionExpectation(double value, double? alternative, string quantity)
    {
        Value = value;
        Alternative = alternative;
        Quantity = quantity;
    }

    public double Value { get; }

    public double? Alternative { get; }

    /// <summary>What was measured, for messages: "the horizontal distance between the line and the circle's centre".</summary>
    public string Quantity { get; }
}

/// <summary>
/// The arithmetic of drawing annotations (design 611, Dimensions and annotations), without SOLIDWORKS: what a reference
/// dimension between entities should read, measured from the model's geometry drawn through the view's transform
/// (ViewGeometry.Draw) and divided by the view's scale, so a dimension is checked against the model rather than against
/// what SOLIDWORKS answered; which circles get centre marks and which cylinders centre lines; and the small rules of
/// datum labels, tolerance text and model dimension names.
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>A dimension's value agrees with the model's within this, mm (design 611).</summary>
    public const double ValueToleranceMm = 0.001;

    /// <summary>An angle agrees with the model's within this, degrees (design 611).</summary>
    public const double AngleToleranceDeg = 0.01;

    /// <summary>Two drawn lines are parallel when the sine of the angle between them is within this.</summary>
    const double ParallelSine = 1e-6;

    /// <summary>
    /// What a <paramref name="kind"/> dimension between <paramref name="drawn"/> (one or two entities as the view draws them,
    /// sheet mm) reads, model mm or degrees, at the view's <paramref name="scale"/>; null with <paramref name="why"/> when
    /// those entities do not make that kind of dimension. A point stands for itself, a circle or arc for its centre:
    /// radius and diameter take one circle or arc; angle two lines that are not parallel; linear one line (its length), two
    /// parallel lines (the distance between them), or a line and a point (the point's distance from the line), or two
    /// points (the distance between them); horizontal and vertical one line (its run across or up the sheet), or two of a
    /// point and a line square to the direction measured.
    /// </summary>
    public static DimensionExpectation? Expect(DimensionKind kind, IReadOnlyList<ViewEntityInfo> drawn, double scale, out string why)
    {
        why = "";
        if (drawn.Count < 1 || drawn.Count > 2)
        {
            why = $"a dimension takes one or two entities, not {drawn.Count}";
            return null;
        }
        for (int i = 0; i < drawn.Count; i++)
        {
            if (drawn[i].Shape == DrawnShape.Other)
            {
                why = $"entity {i} is drawn as neither a line, a circle or arc, nor a point, so the add-in cannot say what it measures";
                return null;
            }
        }
        switch (kind)
        {
            case DimensionKind.Radius:
            case DimensionKind.Diameter:
                if (drawn.Count != 1 || (drawn[0].Shape != DrawnShape.Circle && drawn[0].Shape != DrawnShape.Arc) || drawn[0].RadiusMm == null)
                {
                    why = $"a {Wire(kind)} dimension takes one circular edge the view draws as a circle or arc";
                    return null;
                }
                double r = drawn[0].RadiusMm!.Value / scale;
                return kind == DimensionKind.Radius ? new DimensionExpectation(r, null, "the circle's radius") : new DimensionExpectation(2 * r, null, "the circle's diameter");
            case DimensionKind.Angle:
                if (drawn.Count != 2 || drawn[0].Shape != DrawnShape.Line || drawn[1].Shape != DrawnShape.Line)
                {
                    why = "an angle dimension takes two straight edges the view draws as lines";
                    return null;
                }
                var (ua, ub) = (Direction(drawn[0]), Direction(drawn[1]));
                double sine = Math.Abs(ua.X * ub.Y - ua.Y * ub.X);
                if (sine <= ParallelSine)
                {
                    why = "the two lines are drawn parallel, so they make no angle; ask for linear to dimension the distance between them";
                    return null;
                }
                double degrees = Math.Acos(Math.Max(-1, Math.Min(1, Math.Abs(ua.X * ub.X + ua.Y * ub.Y)))) * 180 / Math.PI;
                return new DimensionExpectation(degrees, Math.Abs(180 - 2 * degrees) <= AngleToleranceDeg ? (double?)null : 180 - degrees, "the angle between the two lines");
            case DimensionKind.Linear:
                return Linear(drawn, scale, out why);
            default:
                return Along(kind == DimensionKind.Horizontal, drawn, scale, out why);
        }
    }

    static DimensionExpectation? Linear(IReadOnlyList<ViewEntityInfo> drawn, double scale, out string why)
    {
        why = "";
        if (drawn.Count == 1)
        {
            if (drawn[0].Shape != DrawnShape.Line)
            {
                why = "a linear dimension of one entity takes a straight edge (its length); for a circle ask for radius or diameter";
                return null;
            }
            return Positive(Distance(drawn[0].Start!, drawn[0].End!) / scale, "the line's length", out why);
        }
        var (a, b) = (drawn[0], drawn[1]);
        if (a.Shape == DrawnShape.Line && b.Shape == DrawnShape.Line)
        {
            var (ua, ub) = (Direction(a), Direction(b));
            if (Math.Abs(ua.X * ub.Y - ua.Y * ub.X) > ParallelSine)
            {
                why = "the two lines are not drawn parallel, so there is no one distance between them; ask for angle, or dimension a line and a point";
                return null;
            }
            return Positive(FromLine(a, b.Start!) / scale, "the distance between the two parallel lines", out why);
        }
        if (a.Shape == DrawnShape.Line) return Positive(FromLine(a, Anchor(b)) / scale, $"the distance of the {Point(b)} from the line", out why);
        if (b.Shape == DrawnShape.Line) return Positive(FromLine(b, Anchor(a)) / scale, $"the distance of the {Point(a)} from the line", out why);
        return Positive(Distance(Anchor(a), Anchor(b)) / scale, $"the distance between the {Point(a)} and the {Point(b)}", out why);
    }

    static DimensionExpectation? Along(bool horizontal, IReadOnlyList<ViewEntityInfo> drawn, double scale, out string why)
    {
        why = "";
        string way = horizontal ? "horizontal" : "vertical";
        double Coordinate(SheetPoint p) => horizontal ? p.X : p.Y;
        if (drawn.Count == 1)
        {
            if (drawn[0].Shape != DrawnShape.Line)
            {
                why = $"a {way} dimension of one entity takes a straight edge (its run {(horizontal ? "across" : "up")} the sheet)";
                return null;
            }
            return Positive(Math.Abs(Coordinate(drawn[0].End!) - Coordinate(drawn[0].Start!)) / scale, $"the line's {way} run", out why);
        }
        var at = new double[2];
        for (int i = 0; i < 2; i++)
        {
            var e = drawn[i];
            if (e.Shape != DrawnShape.Line)
            {
                at[i] = Coordinate(Anchor(e));
                continue;
            }
            double run = Math.Abs(Coordinate(e.End!) - Coordinate(e.Start!)), length = Distance(e.Start!, e.End!);
            if (run > ParallelSine * Math.Max(1, length))
            {
                why = $"a {way} dimension between two entities measures from a point, a circle's centre or a line drawn {(horizontal ? "vertical" : "horizontal")}; entity {i} is a line that is not";
                return null;
            }
            at[i] = Coordinate(e.Start!);
        }
        return Positive(Math.Abs(at[0] - at[1]) / scale, $"the {way} distance between {Describe(drawn[0])} and {Describe(drawn[1])}", out why);
    }

    static DimensionExpectation? Positive(double value, string quantity, out string why)
    {
        why = "";
        if (value > ValueToleranceMm) return new DimensionExpectation(value, null, quantity);
        why = $"{quantity} is 0: the entities are drawn at the same place, so there is nothing to dimension";
        return null;
    }

    /// <summary>True when <paramref name="measured"/> is the value expected (or an angle's supplement) within the tolerance.</summary>
    public static bool Matches(DimensionExpectation expected, double measured, bool angle)
    {
        double tolerance = angle ? AngleToleranceDeg : ValueToleranceMm;
        return Math.Abs(measured - expected.Value) <= tolerance || (expected.Alternative is double other && Math.Abs(measured - other) <= tolerance);
    }

    /// <summary>
    /// The circles a view draws that take a centre mark, in order: each entity drawn as a circle or arc, each centre once
    /// (a hole's front and back edges share one), within 0.01 mm.
    /// </summary>
    public static List<T> DistinctCentres<T>(IEnumerable<T> items, Func<T, ViewEntityInfo> drawn)
    {
        var kept = new List<T>();
        var centres = new List<SheetPoint>();
        foreach (var item in items)
        {
            var d = drawn(item);
            if ((d.Shape != DrawnShape.Circle && d.Shape != DrawnShape.Arc) || d.Centre == null) continue;
            if (centres.Any(c => ViewGeometry.Near(c, d.Centre))) continue;
            centres.Add(d.Centre);
            kept.Add(item);
        }
        return kept;
    }

    /// <summary>True when a view sees a cylinder of this axis from the side: the axis lies square to the view's facing.</summary>
    public static bool SeenFromSide(ViewTransform t, Vector3 axis)
    {
        var f = ViewGeometry.Facing(t);
        double n = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        return n > 0 && Math.Abs((f.X * axis.X + f.Y * axis.Y + f.Z * axis.Z) / n) <= ViewGeometry.DirectionTolerance;
    }

    /// <summary>
    /// The cylinders a view sees from the side that take a centre line, in order: each axis once (SOLIDWORKS splits a
    /// hole's cylinder in two faces on one axis), comparing where the view draws each axis within 0.01 mm.
    /// </summary>
    public static List<T> DistinctAxes<T>(ViewTransform t, IEnumerable<T> items, Func<T, (Point3 Origin, Vector3 Axis)> axis)
    {
        var kept = new List<T>();
        var lines = new List<(SheetPoint Point, SheetPoint Direction)>();
        foreach (var item in items)
        {
            var (origin, direction) = axis(item);
            if (!SeenFromSide(t, direction)) continue;
            var p = ViewGeometry.ToSheet(t, origin);
            var q = ViewGeometry.ToSheet(t, new Point3(origin.X + direction.X, origin.Y + direction.Y, origin.Z + direction.Z));
            var d = Unit(new SheetPoint(q.X - p.X, q.Y - p.Y));
            if (lines.Any(l => Math.Abs(l.Direction.X * d.Y - l.Direction.Y * d.X) <= ParallelSine && OffLine(l.Point, l.Direction, p) <= ViewGeometry.PositionToleranceMm)) continue;
            lines.Add((p, d));
            kept.Add(item);
        }
        return kept;
    }

    /// <summary>True when two entities are lines the view draws parallel, so a centre line goes between them.</summary>
    public static bool Parallel(ViewEntityInfo a, ViewEntityInfo b)
    {
        if (a.Shape != DrawnShape.Line || b.Shape != DrawnShape.Line) return false;
        var (ua, ub) = (Direction(a), Direction(b));
        return Math.Abs(ua.X * ub.Y - ua.Y * ub.X) <= ParallelSine && FromLine(a, b.Start!) > ViewGeometry.PositionToleranceMm;
    }

    /// <summary>The form tolerances, which never take a datum (ISO 1101, ASME Y14.5).</summary>
    public static bool IsForm(GtolCharacteristic characteristic) =>
        characteristic == GtolCharacteristic.Straightness || characteristic == GtolCharacteristic.Flatness
        || characteristic == GtolCharacteristic.Circularity || characteristic == GtolCharacteristic.Cylindricity;

    /// <summary>A datum label as the add-in writes it: upper case, trimmed; null when it is not 1 to 3 letters A to Z without I, O and Q (ASME Y14.5).</summary>
    public static string? DatumLabel(string? label)
    {
        string l = (label ?? "").Trim().ToUpperInvariant();
        if (l.Length < 1 || l.Length > 3) return null;
        return l.All(c => c >= 'A' && c <= 'Z' && c != 'I' && c != 'O' && c != 'Q') ? l : null;
    }

    /// <summary>A tolerance as a frame's text: mm, up to six decimals, invariant (0.1, 0.025).</summary>
    public static string ToleranceText(double mm) => mm.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The number in a frame's tolerance text (SOLIDWORKS may put a symbol code before it); null when there is none.</summary>
    public static double? ParseTolerance(string? text)
    {
        var m = Regex.Match(text ?? "", @"[-+]?\d*\.?\d+");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
    }

    /// <summary>
    /// True for a dimension of the model, false for one the drawing made (its name's owner, after the @, is one of the
    /// drawing's views or sheets: RD1@Drawing View1).
    /// </summary>
    public static bool IsModelDimension(string fullName, IEnumerable<string> viewsAndSheets)
    {
        var parts = (fullName ?? "").Split('@');
        return parts.Length < 2 || !viewsAndSheets.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    static SheetPoint Anchor(ViewEntityInfo e) => e.Shape == DrawnShape.Point ? e.Start! : e.Centre!;

    static string Point(ViewEntityInfo e) => e.Shape == DrawnShape.Point ? "point" : "circle's centre";

    static string Describe(ViewEntityInfo e) => e.Shape == DrawnShape.Line ? "the line" : "the " + Point(e);

    static SheetPoint Direction(ViewEntityInfo line) => Unit(new SheetPoint(line.End!.X - line.Start!.X, line.End.Y - line.Start.Y));

    static SheetPoint Unit(SheetPoint v)
    {
        double n = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return n == 0 ? v : new SheetPoint(v.X / n, v.Y / n);
    }

    static double Distance(SheetPoint a, SheetPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>How far <paramref name="p"/> is from the infinite line through <paramref name="line"/>, sheet mm.</summary>
    static double FromLine(ViewEntityInfo line, SheetPoint p) => OffLine(line.Start!, Direction(line), p);

    static double OffLine(SheetPoint on, SheetPoint direction, SheetPoint p) => Math.Abs((p.X - on.X) * direction.Y - (p.Y - on.Y) * direction.X);

    static string Wire(Enum value) => WireNames.ForEnum(value);
}
