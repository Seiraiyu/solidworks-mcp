using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// A drawing view's model-to-sheet transform (IView.ModelToViewTransform), in millimetres: a model point p (a row vector,
/// model mm) lands on the sheet at (p·R)·scale + t, sheet mm from the sheet's lower-left corner. R is the 3x3 rotation
/// row-major, whose columns are the model directions the sheet's right, up and toward-the-reader point along. CADapter
/// measured this layout on SOLIDWORKS 2026 (IMathTransform.ArrayData: nine rotation numbers, the translation in metres,
/// the scale); plan 5.A's live run proves it again through the add-in.
/// </summary>
public sealed class ViewTransform
{
    public ViewTransform(IReadOnlyList<double> rotation, double txMm, double tyMm, double tzMm, double scale)
    {
        if (rotation.Count != 9) throw new ArgumentException("a view's rotation has nine numbers", nameof(rotation));
        Rotation = rotation.ToArray();
        TxMm = txMm;
        TyMm = tyMm;
        TzMm = tzMm;
        Scale = scale;
    }

    /// <summary>R row-major: R[i][j] is Rotation[3 * i + j].</summary>
    public double[] Rotation { get; }

    public double TxMm { get; }

    public double TyMm { get; }

    public double TzMm { get; }

    public double Scale { get; }

    /// <summary>
    /// From IMathTransform.ArrayData's sixteen numbers: the rotation, the translation in metres, the scale (the last three
    /// are unused). Null when SOLIDWORKS gave fewer than thirteen.
    /// </summary>
    public static ViewTransform? FromArrayData(IReadOnlyList<double>? data)
    {
        if (data == null || data.Count < 13) return null;
        return new ViewTransform(data.Take(9).ToList(), data[9] * 1000, data[10] * 1000, data[11] * 1000, data[12]);
    }

    public override string ToString() =>
        System.FormattableString.Invariant($"R [{string.Join(" ", Rotation.Select(r => r.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}], t ({TxMm:0.###}, {TyMm:0.###}) mm, scale {Scale:0.######}");
}

/// <summary>
/// The arithmetic of drawing views (design 611, Views), without SOLIDWORKS: where a model point lands on the sheet, which
/// model direction a view shows facing the reader, the box a model's bounding box projects to, and whether a view's
/// orientation is the one asked. Lengths in mm: model mm in, sheet mm out.
/// </summary>
public static class ViewGeometry
{
    /// <summary>Positions and extents agree within this, sheet mm (design 611, How a view is checked).</summary>
    public const double PositionToleranceMm = 0.01;

    /// <summary>Directions agree within this, per component of a unit vector.</summary>
    public const double DirectionTolerance = 1e-6;

    /// <summary>The six orthographic orientations and the model direction each shows facing the reader.</summary>
    public static readonly IReadOnlyDictionary<string, Vector3> Orthographic = new Dictionary<string, Vector3>(StringComparer.Ordinal)
    {
        ["front"] = new Vector3(0, 0, 1),
        ["back"] = new Vector3(0, 0, -1),
        ["top"] = new Vector3(0, 1, 0),
        ["bottom"] = new Vector3(0, -1, 0),
        ["right"] = new Vector3(1, 0, 0),
        ["left"] = new Vector3(-1, 0, 0),
    };

    /// <summary>The standard orientations view.create names: the six orthographic ones and the three pictorial.</summary>
    public static readonly IReadOnlyList<string> Standard = new[] { "front", "back", "left", "right", "top", "bottom", "isometric", "dimetric", "trimetric" };

    /// <summary>Where model point <paramref name="p"/> (model mm) is drawn, sheet mm.</summary>
    public static SheetPoint ToSheet(ViewTransform t, Point3 p)
    {
        var q = Rotate(t, p);
        return new SheetPoint(q.X * t.Scale + t.TxMm, q.Y * t.Scale + t.TyMm);
    }

    /// <summary>p·R: the model point in the view's own axes (right, up, toward the reader), model mm.</summary>
    public static Vector3 Rotate(ViewTransform t, Point3 p)
    {
        var r = t.Rotation;
        return new Vector3(
            p.X * r[0] + p.Y * r[3] + p.Z * r[6],
            p.X * r[1] + p.Y * r[4] + p.Z * r[7],
            p.X * r[2] + p.Y * r[5] + p.Z * r[8]);
    }

    /// <summary>The model direction that points to the sheet's right (R's first column).</summary>
    public static Vector3 Right(ViewTransform t) => new Vector3(t.Rotation[0], t.Rotation[3], t.Rotation[6]);

    /// <summary>The model direction that points up the sheet (R's second column).</summary>
    public static Vector3 Up(ViewTransform t) => new Vector3(t.Rotation[1], t.Rotation[4], t.Rotation[7]);

    /// <summary>The model direction that faces the reader (R's third column): +Z for a front view.</summary>
    public static Vector3 Facing(ViewTransform t) => new Vector3(t.Rotation[2], t.Rotation[5], t.Rotation[8]);

    /// <summary>The rectangle a model box (model mm) is drawn in: its eight corners through the transform, sheet mm.</summary>
    public static SheetBox Project(ViewTransform t, Box3 box)
    {
        var corners = Corners(box).Select(c => ToSheet(t, c)).ToList();
        return new SheetBox
        {
            Min = new SheetPoint(corners.Min(c => c.X), corners.Min(c => c.Y)),
            Max = new SheetPoint(corners.Max(c => c.X), corners.Max(c => c.Y)),
        };
    }

    /// <summary>How deep a model box is seen from the reader, sheet mm at the view's scale (the width a view projected beside it shows).</summary>
    public static double Depth(ViewTransform t, Box3 box)
    {
        var depths = Corners(box).Select(c => Rotate(t, c).Z).ToList();
        return (depths.Max() - depths.Min()) * t.Scale;
    }

    /// <summary>The standard orientation a view facing <paramref name="facing"/> is: one of the six, isometric, or null for any other.</summary>
    public static string? Orientation(Vector3 facing)
    {
        foreach (var o in Orthographic)
        {
            if (Same(o.Value, facing)) return o.Key;
        }
        return Isometric(facing) ? "isometric" : null;
    }

    /// <summary>
    /// Null when a view with transform <paramref name="t"/> shows <paramref name="orientation"/> as design 611 checks it:
    /// for the six orthographic orientations the model direction facing the reader is exactly theirs; for isometric its
    /// three components are equal in size. Dimetric, trimetric and named views are not checked by direction (null).
    /// Otherwise what was measured.
    /// </summary>
    public static string? OrientationMismatch(string orientation, ViewTransform t)
    {
        var facing = Facing(t);
        if (Orthographic.TryGetValue(orientation, out var expected))
            return Same(expected, facing) ? null : $"a {orientation} view faces the model's {Describe(expected)} toward the reader, but this one faces {Describe(facing)}";
        if (orientation == "isometric")
            return Isometric(facing) ? null : $"an isometric view faces a direction whose three components are equal in size, but this one faces {Describe(facing)}";
        return null;
    }

    /// <summary>True when the facing direction's three components are equal in size (each 1/√3).</summary>
    public static bool Isometric(Vector3 facing)
    {
        double third = 1 / Math.Sqrt(3);
        return Math.Abs(Math.Abs(facing.X) - third) <= DirectionTolerance
            && Math.Abs(Math.Abs(facing.Y) - third) <= DirectionTolerance
            && Math.Abs(Math.Abs(facing.Z) - third) <= DirectionTolerance;
    }

    /// <summary>The same direction, component by component, within <see cref="DirectionTolerance"/>.</summary>
    public static bool Same(Vector3 a, Vector3 b) =>
        Math.Abs(a.X - b.X) <= DirectionTolerance && Math.Abs(a.Y - b.Y) <= DirectionTolerance && Math.Abs(a.Z - b.Z) <= DirectionTolerance;

    /// <summary>Two sheet points within <paramref name="toleranceMm"/> on each axis.</summary>
    public static bool Near(SheetPoint a, SheetPoint b, double toleranceMm = PositionToleranceMm) =>
        Math.Abs(a.X - b.X) <= toleranceMm && Math.Abs(a.Y - b.Y) <= toleranceMm;

    /// <summary>Two boxes whose corners agree within <paramref name="toleranceMm"/>.</summary>
    public static bool SameBox(SheetBox a, SheetBox b, double toleranceMm = PositionToleranceMm) =>
        Near(a.Min, b.Min, toleranceMm) && Near(a.Max, b.Max, toleranceMm);

    /// <summary>Two boxes of the same width and height within <paramref name="toleranceMm"/>, wherever they are.</summary>
    public static bool SameSize(SheetBox a, SheetBox b, double toleranceMm = PositionToleranceMm) =>
        Math.Abs(Width(a) - Width(b)) <= toleranceMm && Math.Abs(Height(a) - Height(b)) <= toleranceMm;

    /// <summary><paramref name="inner"/> lies inside <paramref name="outer"/>, allowing <paramref name="toleranceMm"/>.</summary>
    public static bool Within(SheetBox inner, SheetBox outer, double toleranceMm = PositionToleranceMm) =>
        inner.Min.X >= outer.Min.X - toleranceMm && inner.Min.Y >= outer.Min.Y - toleranceMm
        && inner.Max.X <= outer.Max.X + toleranceMm && inner.Max.Y <= outer.Max.Y + toleranceMm;

    public static double Width(SheetBox box) => box.Max.X - box.Min.X;

    public static double Height(SheetBox box) => box.Max.Y - box.Min.Y;

    /// <summary>A box's centre.</summary>
    public static SheetPoint Centre(SheetBox box) => new SheetPoint((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2);

    /// <summary>True for a view projected to the left or right (it shares its parent's horizontal centre line).</summary>
    public static bool Horizontal(ProjectDirection direction) => direction == ProjectDirection.Left || direction == ProjectDirection.Right;

    /// <summary>
    /// The model direction a view projected from <paramref name="parent"/> shows facing the reader: the parent's right
    /// for a view from the right, its up for a view from above, and their opposites. The view is the parent turned 90°.
    /// </summary>
    public static Vector3 ProjectedFacing(ViewTransform parent, ProjectDirection direction) => direction switch
    {
        ProjectDirection.Right => Right(parent),
        ProjectDirection.Left => Negate(Right(parent)),
        ProjectDirection.Above => Up(parent),
        _ => Negate(Up(parent)),
    };

    /// <summary>
    /// Which side of its parent a projected view is drawn on, as unit steps on the sheet (x, y): in third-angle projection
    /// the view from the right goes on the right and the view from above above; in first-angle projection the opposite.
    /// </summary>
    public static (int Dx, int Dy) Side(ProjectDirection direction, Projection projection)
    {
        int sign = projection == Projection.ThirdAngle ? 1 : -1;
        return direction switch
        {
            ProjectDirection.Right => (sign, 0),
            ProjectDirection.Left => (-sign, 0),
            ProjectDirection.Above => (0, sign),
            _ => (0, -sign),
        };
    }

    /// <summary>
    /// Where a projected view goes when no position is asked: beside its parent on the side <see cref="Side"/> names, on its
    /// centre line, with <paramref name="gapMm"/> between the parent's drawn extents and the new view's, which is
    /// <paramref name="depthMm"/> across (<see cref="Depth"/> of the parent).
    /// </summary>
    public static SheetPoint ProjectedPlace(SheetPoint parentPosition, SheetBox parentExtents, double depthMm, ProjectDirection direction, Projection projection, double gapMm)
    {
        var (dx, dy) = Side(direction, projection);
        var centre = Centre(parentExtents);
        if (dx != 0)
        {
            double reach = dx > 0 ? parentExtents.Max.X - centre.X : centre.X - parentExtents.Min.X;
            return new SheetPoint(centre.X + dx * (reach + gapMm + depthMm / 2), parentPosition.Y);
        }
        double rise = dy > 0 ? parentExtents.Max.Y - centre.Y : centre.Y - parentExtents.Min.Y;
        return new SheetPoint(parentPosition.X, centre.Y + dy * (rise + gapMm + depthMm / 2));
    }

    /// <summary>
    /// Null when <paramref name="at"/> is on the side of <paramref name="parentPosition"/> a view projected
    /// <paramref name="direction"/> goes under <paramref name="projection"/>; otherwise why not.
    /// </summary>
    public static string? WrongSide(SheetPoint parentPosition, SheetPoint at, ProjectDirection direction, Projection projection)
    {
        var (dx, dy) = Side(direction, projection);
        double along = dx != 0 ? (at.X - parentPosition.X) * dx : (at.Y - parentPosition.Y) * dy;
        if (along > PositionToleranceMm) return null;
        string side = dx > 0 ? "right of" : dx < 0 ? "left of" : dy > 0 ? "above" : "below";
        return $"a view seen from {Wire(direction)} goes {side} its parent in {Wire(projection)} projection";
    }

    /// <summary>
    /// Null when a view projected <paramref name="direction"/> from <paramref name="parent"/> is the parent turned 90° that
    /// way and shares its centre line: it faces <see cref="ProjectedFacing"/>, and the model's origin is drawn at the same
    /// height in both (left and right) or the same x (above and below), within 0.01 mm. Otherwise what was measured.
    /// </summary>
    public static string? AlignmentMismatch(ViewTransform parent, ViewTransform child, ProjectDirection direction)
    {
        var expected = ProjectedFacing(parent, direction);
        var facing = Facing(child);
        if (!Same(expected, facing)) return $"a view projected from {Wire(direction)} should face {expected} toward the reader, but faces {facing}";
        var origin = new Point3(0, 0, 0);
        var a = ToSheet(parent, origin);
        var b = ToSheet(child, origin);
        if (Horizontal(direction))
        {
            return Math.Abs(a.Y - b.Y) <= PositionToleranceMm ? null
                : System.FormattableString.Invariant($"it is not on its parent's horizontal centre line: the model's origin is drawn at y {b.Y:0.###} mm in it and {a.Y:0.###} mm in its parent");
        }
        return Math.Abs(a.X - b.X) <= PositionToleranceMm ? null
            : System.FormattableString.Invariant($"it is not on its parent's vertical centre line: the model's origin is drawn at x {b.X:0.###} mm in it and {a.X:0.###} mm in its parent");
    }

    /// <summary>
    /// What a view draws, as the box around its edges on the sheet, from IView.GetPolylines5's numbers: for each edge its
    /// type, the count of geometry numbers and those numbers, six style numbers (colour, line style, font, weight, layer,
    /// layer override), the count of points, then each point's x, y and z in metres in the view's own coordinates about
    /// its position; a point is drawn at position + point × scale (CADapter's measurement on SOLIDWORKS 2026; plan 5.A's
    /// live run proves it again). Null when the view draws no point. FormatException when the numbers run short.
    /// </summary>
    public static SheetBox? DrawnExtents(IReadOnlyList<double> polylines, SheetPoint position, double scale)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        int i = 0;
        while (i < polylines.Count)
        {
            int geometry = Count(polylines, i + 1, "geometry numbers");
            i += 2 + geometry + 6;
            int points = Count(polylines, i, "points");
            i++;
            if (i + 3 * points > polylines.Count) throw new FormatException($"the polylines end inside an edge's {points} points (at number {i})");
            for (int p = 0; p < points; p++, i += 3)
            {
                double x = position.X + polylines[i] * 1000 * scale;
                double y = position.Y + polylines[i + 1] * 1000 * scale;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                any = true;
            }
        }
        return any ? new SheetBox { Min = new SheetPoint(minX, minY), Max = new SheetPoint(maxX, maxY) } : null;
    }

    static int Count(IReadOnlyList<double> numbers, int at, string what)
    {
        if (at >= numbers.Count) throw new FormatException($"the polylines end where an edge's count of {what} should be (number {at})");
        double value = numbers[at];
        if (value < 0 || value != Math.Floor(value)) throw new FormatException($"number {at} should count an edge's {what}, but is {value}");
        return (int)value;
    }

    /// <summary>
    /// How a model entity (described in model mm, as model.find describes it) is drawn by a view, in sheet mm: a vertex
    /// as a point; a straight edge as a line from end to end (a point when seen end on); a circular edge seen face on as
    /// a circle or arc, its radius times the view's scale, and seen edge on as a line across its diameter; anything else
    /// as other, with no geometry. Only Shape, Start, End, Centre and RadiusMm are filled.
    /// </summary>
    public static ViewEntityInfo Draw(ViewTransform t, EntityInfo entity, bool vertex)
    {
        var drawn = new ViewEntityInfo { Shape = DrawnShape.Other };
        var facing = Facing(t);
        if (vertex || entity.Geometry == GeometryType.Point)
        {
            if (entity.Origin == null) return drawn;
            drawn.Shape = DrawnShape.Point;
            drawn.Start = ToSheet(t, entity.Origin);
            return drawn;
        }
        if (entity.Geometry == GeometryType.Line && entity.Origin != null && entity.Axis != null && entity.LengthMm != null)
        {
            var half = Scaled(Unit(entity.Axis), entity.LengthMm.Value / 2);
            var start = ToSheet(t, Offset(entity.Origin, half, -1));
            var end = ToSheet(t, Offset(entity.Origin, half, 1));
            if (Near(start, end, 1e-9)) return new ViewEntityInfo { Shape = DrawnShape.Point, Start = start };
            drawn.Shape = DrawnShape.Line;
            drawn.Start = start;
            drawn.End = end;
            return drawn;
        }
        if (entity.Geometry == GeometryType.Circle && entity.Origin != null && entity.Axis != null && entity.RadiusMm != null)
        {
            double along = Dot(Unit(entity.Axis), facing);
            if (Math.Abs(Math.Abs(along) - 1) <= DirectionTolerance)
            {
                bool whole = entity.LengthMm == null || Math.Abs(entity.LengthMm.Value - 2 * Math.PI * entity.RadiusMm.Value) <= 1e-6 * Math.Max(1, entity.RadiusMm.Value);
                drawn.Shape = whole ? DrawnShape.Circle : DrawnShape.Arc;
                drawn.Centre = ToSheet(t, entity.Origin);
                drawn.RadiusMm = entity.RadiusMm.Value * t.Scale;
                return drawn;
            }
            if (Math.Abs(along) <= DirectionTolerance && (entity.LengthMm == null || Math.Abs(entity.LengthMm.Value - 2 * Math.PI * entity.RadiusMm.Value) <= 1e-6 * Math.Max(1, entity.RadiusMm.Value)))
            {
                var across = Scaled(Unit(Cross(facing, entity.Axis)), entity.RadiusMm.Value);
                drawn.Shape = DrawnShape.Line;
                drawn.Start = ToSheet(t, Offset(entity.Origin, across, -1));
                drawn.End = ToSheet(t, Offset(entity.Origin, across, 1));
            }
        }
        return drawn;
    }

    static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    static Vector3 Unit(Vector3 v)
    {
        double n = Math.Sqrt(Dot(v, v));
        return n == 0 ? v : new Vector3(v.X / n, v.Y / n, v.Z / n);
    }

    static Vector3 Scaled(Vector3 v, double by) => new Vector3(v.X * by, v.Y * by, v.Z * by);

    static Point3 Offset(Point3 p, Vector3 by, int sign) => new Point3(p.X + sign * by.X, p.Y + sign * by.Y, p.Z + sign * by.Z);

    static Vector3 Negate(Vector3 v) => new Vector3(-v.X, -v.Y, -v.Z);

    static string Wire(System.Enum value) => SwAgent.Contracts.WireNames.ForEnum(value);

    static IEnumerable<Point3> Corners(Box3 box)
    {
        foreach (double x in new[] { box.Min.X, box.Max.X })
        {
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            {
                foreach (double z in new[] { box.Min.Z, box.Max.Z }) yield return new Point3(x, y, z);
            }
        }
    }

    static string Describe(Vector3 v) =>
        Orthographic.FirstOrDefault(o => Same(o.Value, v)).Key is string name ? $"{Sign(v)} ({name})" : v.ToString();

    static string Sign(Vector3 v) =>
        Math.Abs(v.X) > 0.5 ? (v.X > 0 ? "+X" : "-X") : Math.Abs(v.Y) > 0.5 ? (v.Y > 0 ? "+Y" : "-Y") : (v.Z > 0 ? "+Z" : "-Z");
}
