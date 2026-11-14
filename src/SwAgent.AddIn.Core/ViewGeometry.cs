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
