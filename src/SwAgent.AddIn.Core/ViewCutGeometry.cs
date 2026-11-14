using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>A face a section view hatches (IView.GetFaceHatches), read for the plane SOLIDWORKS cut. Model mm.</summary>
public sealed class CutFace
{
    public CutFace(Vector3? normal, Point3? point, double areaMm2)
    {
        Normal = normal;
        Point = point;
        AreaMm2 = areaMm2;
    }

    /// <summary>Its normal; null when SOLIDWORKS gives the hatch no planar face.</summary>
    public Vector3? Normal { get; }

    /// <summary>A point on it (the one nearest the model's origin); null when SOLIDWORKS gives the hatch no face.</summary>
    public Point3? Point { get; }

    public double AreaMm2 { get; }
}

/// <summary>
/// The arithmetic of section and detail views (design 611, Sections and details), without SOLIDWORKS: where a sheet
/// point lies in a view's own sketch, the section line the add-in draws across a view, where a section view goes and
/// how it is aligned, and whether the faces it hatches lie in a plane through the point asked. Model mm in, sheet mm out.
/// </summary>
public static class ViewCutGeometry
{
    /// <summary>How far a section line runs past what its view draws, at each end, sheet mm.</summary>
    public const double OverrunMm = 5;

    /// <summary>The faces a section hatches lie in a plane through the point asked within this, model mm.</summary>
    public const double PlaneToleranceMm = 0.01;

    /// <summary>
    /// Where a sheet point lies in a view's own sketch: model mm about the view's position (CADapter measured the view's
    /// sketch in model units about its position, not in sheet coordinates; the frame IView.GetPolylines5 gives its points
    /// in, which the 5.A live run proved). The inverse of <see cref="FromViewSketch"/>.
    /// </summary>
    public static Point2 ToViewSketch(SheetPoint position, double scale, SheetPoint sheet) =>
        new Point2((sheet.X - position.X) / scale, (sheet.Y - position.Y) / scale);

    /// <summary>Where a point of a view's own sketch (model mm about its position) is drawn, sheet mm.</summary>
    public static SheetPoint FromViewSketch(SheetPoint position, double scale, Point2 sketch) =>
        new SheetPoint(position.X + sketch.X * scale, position.Y + sketch.Y * scale);

    /// <summary>
    /// The section line the add-in draws across a view with transform <paramref name="t"/> that draws
    /// <paramref name="extents"/>: through where <paramref name="through"/> (model mm) is drawn, horizontal or vertical,
    /// from <see cref="OverrunMm"/> before the extents to as far after them, sheet mm. Null when that point is not drawn
    /// strictly inside the extents across the line, so the line would cut nothing.
    /// </summary>
    public static (SheetPoint From, SheetPoint To)? CutLine(ViewTransform t, SheetBox extents, Point3 through, CutOrientation orientation)
    {
        var at = ViewGeometry.ToSheet(t, through);
        double tol = ViewGeometry.PositionToleranceMm;
        if (orientation == CutOrientation.Horizontal)
        {
            if (at.Y <= extents.Min.Y + tol || at.Y >= extents.Max.Y - tol) return null;
            return (new SheetPoint(extents.Min.X - OverrunMm, at.Y), new SheetPoint(extents.Max.X + OverrunMm, at.Y));
        }
        if (at.X <= extents.Min.X + tol || at.X >= extents.Max.X - tol) return null;
        return (new SheetPoint(at.X, extents.Max.Y + OverrunMm), new SheetPoint(at.X, extents.Min.Y - OverrunMm));
    }

    /// <summary>
    /// Where a section view goes when no position is asked: below its parent for a horizontal line, to its right for a
    /// vertical one, on the parent's centre line, <paramref name="gapMm"/> from the parent's drawn extents, the section
    /// being <paramref name="depthMm"/> across (<see cref="ViewGeometry.Depth"/> of the parent).
    /// </summary>
    public static SheetPoint SectionPlace(SheetPoint parentPosition, SheetBox parentExtents, double depthMm, CutOrientation orientation, double gapMm) =>
        orientation == CutOrientation.Horizontal
            ? new SheetPoint(parentPosition.X, parentExtents.Min.Y - gapMm - depthMm / 2)
            : new SheetPoint(parentExtents.Max.X + gapMm + depthMm / 2, parentPosition.Y);

    /// <summary>True when a section view cut <paramref name="orientation"/> shares its parent's vertical centre line (a horizontal line), false for its horizontal one.</summary>
    public static bool SharesX(CutOrientation orientation) => orientation == CutOrientation.Horizontal;

    /// <summary>
    /// The model direction a section view of <paramref name="parent"/> looks along, up to its sign (the arrows decide which
    /// way): the parent's up for a horizontal line, its right for a vertical one.
    /// </summary>
    public static Vector3 SectionAxis(ViewTransform parent, CutOrientation orientation) =>
        orientation == CutOrientation.Horizontal ? ViewGeometry.Up(parent) : ViewGeometry.Right(parent);

    /// <summary>
    /// Null when a section view with transform <paramref name="child"/> of <paramref name="parent"/>, cut
    /// <paramref name="orientation"/>, faces along <see cref="SectionAxis"/> (either way) and is aligned with its parent:
    /// the model's origin drawn at the same x in both (a horizontal line) or the same y (a vertical one), within 0.01 mm.
    /// Otherwise what was measured.
    /// </summary>
    public static string? SectionAlignmentMismatch(ViewTransform parent, ViewTransform child, CutOrientation orientation)
    {
        var axis = SectionAxis(parent, orientation);
        var facing = ViewGeometry.Facing(child);
        if (!ViewGeometry.Same(axis, facing) && !ViewGeometry.Same(Negate(axis), facing))
            return $"a section cut {Wire(orientation)} across its parent should face {axis} or its opposite toward the reader, but faces {facing}";
        var origin = new Point3(0, 0, 0);
        var a = ViewGeometry.ToSheet(parent, origin);
        var b = ViewGeometry.ToSheet(child, origin);
        if (SharesX(orientation))
        {
            return Math.Abs(a.X - b.X) <= ViewGeometry.PositionToleranceMm ? null
                : FormattableString.Invariant($"it is not on its parent's vertical centre line: the model's origin is drawn at x {b.X:0.###} mm in it and {a.X:0.###} mm in its parent");
        }
        return Math.Abs(a.Y - b.Y) <= ViewGeometry.PositionToleranceMm ? null
            : FormattableString.Invariant($"it is not on its parent's horizontal centre line: the model's origin is drawn at y {b.Y:0.###} mm in it and {a.Y:0.###} mm in its parent");
    }

    /// <summary>
    /// Null when the section view facing <paramref name="facing"/> hatches at least one face and every face it hatches
    /// lies in a plane square to that direction through <paramref name="through"/>, within
    /// <see cref="PlaneToleranceMm"/>. Otherwise what was measured: no face hatched (the line cut nothing), a hatch with
    /// no planar face (the plane cannot be measured), a face turned another way, or a face off the point.
    /// </summary>
    public static string? CutPlaneMismatch(Vector3 facing, Point3 through, IReadOnlyList<CutFace> faces)
    {
        if (faces.Count == 0) return "it hatches no face: the section line cut nothing";
        foreach (var face in faces)
        {
            if (face.Normal == null || face.Point == null)
                return "SOLIDWORKS gives one of its hatches no planar face, so the cutting plane cannot be measured";
            var n = Unit(face.Normal);
            if (Math.Abs(Math.Abs(Dot(n, facing)) - 1) > ViewGeometry.DirectionTolerance)
                return $"a hatched face is square to {face.Normal}, not to the view's {facing}";
            double off = Math.Abs(Dot(new Vector3(through.X - face.Point.X, through.Y - face.Point.Y, through.Z - face.Point.Z), n));
            if (off > PlaneToleranceMm)
                return FormattableString.Invariant($"a hatched face lies in a plane {off:0.####} mm from {through}, not through it");
        }
        return null;
    }

    /// <summary>
    /// Where a detail view goes when no position is asked: to the right of its parent, centred on the parent's drawn
    /// extents' height, <paramref name="gapMm"/> from them, the detail being <paramref name="drawnRadiusMm"/> in radius
    /// as drawn (the circle's radius times the detail's scale).
    /// </summary>
    public static SheetPoint DetailPlace(SheetBox parentExtents, double drawnRadiusMm, double gapMm) =>
        new SheetPoint(parentExtents.Max.X + gapMm + drawnRadiusMm, ViewGeometry.Centre(parentExtents).Y);

    /// <summary>
    /// Where a detail view with transform <paramref name="detail"/> draws what its parent (transform
    /// <paramref name="parent"/>) draws at <paramref name="drawnInParent"/>, sheet mm: the model point under that point,
    /// at any depth (a detail is turned as its parent, so depth draws nowhere), through the detail. For the circle's
    /// centre this is where a detail is: SOLIDWORKS 2026 SP4 gives a detail's Position as its own origin, about 20 mm from
    /// that point at 2:1 (5.B live run).
    /// </summary>
    public static SheetPoint DetailPoint(ViewTransform parent, ViewTransform detail, SheetPoint drawnInParent)
    {
        double x = (drawnInParent.X - parent.TxMm) / parent.Scale, y = (drawnInParent.Y - parent.TyMm) / parent.Scale;
        var right = ViewGeometry.Right(parent);
        var up = ViewGeometry.Up(parent);
        var model = new Point3(x * right.X + y * up.X, x * right.Y + y * up.Y, x * right.Z + y * up.Z);
        return ViewGeometry.ToSheet(detail, model);
    }

    /// <summary>
    /// Null when a detail view with transform <paramref name="detail"/> shows its parent's model the way the parent does:
    /// the same three model directions to the sheet's right, up and toward the reader. Otherwise what was measured.
    /// </summary>
    public static string? DetailTurnMismatch(ViewTransform parent, ViewTransform detail)
    {
        if (ViewGeometry.Same(ViewGeometry.Right(parent), ViewGeometry.Right(detail)) && ViewGeometry.Same(ViewGeometry.Up(parent), ViewGeometry.Up(detail))
            && ViewGeometry.Same(ViewGeometry.Facing(parent), ViewGeometry.Facing(detail)))
            return null;
        return $"a detail shows its parent's model turned as the parent shows it (right {ViewGeometry.Right(parent)}, up {ViewGeometry.Up(parent)}), but this one is turned right {ViewGeometry.Right(detail)}, up {ViewGeometry.Up(detail)}";
    }

    /// <summary>
    /// Null when every point lies within <paramref name="radiusMm"/> of <paramref name="centre"/> (sheet mm, allowing
    /// 0.01 mm) and there is at least one; otherwise what was measured: nothing drawn, or the farthest point and how far.
    /// </summary>
    public static string? OutsideCircle(IReadOnlyList<SheetPoint> points, SheetPoint centre, double radiusMm)
    {
        if (points.Count == 0) return "it draws nothing";
        var far = points.Select(p => (Point: p, Distance: Math.Sqrt((p.X - centre.X) * (p.X - centre.X) + (p.Y - centre.Y) * (p.Y - centre.Y)))).OrderByDescending(p => p.Distance).First();
        return far.Distance <= radiusMm + ViewGeometry.PositionToleranceMm ? null
            : FormattableString.Invariant($"it draws {far.Point}, {far.Distance:0.###} mm from the circle's centre {centre}, beyond its radius {radiusMm:0.###} mm");
    }

    /// <summary>The letters section and detail labels are given from, in order: A to Z without I, O and Q (ASME Y14.3), then doubled (AA, BB).</summary>
    public static IEnumerable<string> Labels()
    {
        var letters = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).Where(l => l != "I" && l != "O" && l != "Q").ToList();
        foreach (var l in letters) yield return l;
        foreach (var l in letters) yield return l + l;
    }

    /// <summary>The first of <see cref="Labels"/> no section or detail of the drawing has yet (compared case-blind).</summary>
    public static string NextLabel(IEnumerable<string> used)
    {
        var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        return Labels().First(l => !taken.Contains(l));
    }

    static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    static Vector3 Unit(Vector3 v)
    {
        double n = Math.Sqrt(Dot(v, v));
        return n == 0 ? v : new Vector3(v.X / n, v.Y / n, v.Z / n);
    }

    static Vector3 Negate(Vector3 v) => new Vector3(-v.X, -v.Y, -v.Z);

    static string Wire(Enum value) => WireNames.ForEnum(value);
}
