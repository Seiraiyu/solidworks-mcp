using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>A component of an assembly view as the assembly places it collapsed and exploded, and what the view draws of it.</summary>
public sealed class ComponentPlaces
{
    public ComponentPlaces(string name, Transform collapsed, Transform exploded, Box3? explodedBox, Box3? collapsedBox, SheetBox? drawn)
    {
        Name = name;
        Collapsed = collapsed;
        Exploded = exploded;
        ExplodedBox = explodedBox;
        CollapsedBox = collapsedBox;
        Drawn = drawn;
    }

    public string Name { get; }

    public Transform Collapsed { get; }

    public Transform Exploded { get; }

    /// <summary>Its box in the assembly while it is shown exploded, model mm; null when SOLIDWORKS gives none (suppressed).</summary>
    public Box3? ExplodedBox { get; }

    /// <summary>Its box in the assembly collapsed, model mm.</summary>
    public Box3? CollapsedBox { get; }

    /// <summary>The box around the edges the view draws for it, sheet mm; null when it draws none.</summary>
    public SheetBox? Drawn { get; }
}

/// <summary>
/// How an exploded assembly view is checked (design 611, Assembly drawings: exploded, each component's image in the view
/// is displaced from its collapsed image by its explode steps projected through the view's transform). Where the
/// explode steps put each component is read in the assembly itself (its placement and box shown exploded, as explode.list
/// reads a view), and the view must draw each component inside that box projected and scaled. A component the steps
/// move across the sheet (more than 0.01 mm as drawn) must also be drawn outside its collapsed box's image, so a view
/// left collapsed is told apart even where it overlaps. A component whose edges are all hidden draws nothing and is not
/// held to anything; at least one must draw. Containment, not equality: in a view with hidden lines removed, a component
/// another hides draws less than its box (the pin inside the hole), and only exploded does it draw all of itself.
/// </summary>
public static class ExplodedImages
{
    /// <summary>How far a move of the model from <paramref name="from"/> to <paramref name="to"/> (model mm) is drawn, sheet mm.</summary>
    public static SheetPoint Moved(ViewTransform t, Point3 from, Point3 to)
    {
        var a = ViewGeometry.ToSheet(t, from);
        var b = ViewGeometry.ToSheet(t, to);
        return new SheetPoint(b.X - a.X, b.Y - a.Y);
    }

    /// <summary>
    /// Each component's image as results report it, and the first check it fails: null when every one passes, else
    /// (check, component, expected, measured).
    /// </summary>
    public static (List<ExplodedImage> Images, (string Check, string Component, string Expected, string Measured)? Failure) Check(ViewTransform t, IReadOnlyList<ComponentPlaces> components)
    {
        var images = new List<ExplodedImage>();
        (string, string, string, string)? failure = null;
        foreach (var c in components)
        {
            if (c.ExplodedBox == null) continue;
            var moved = Moved(t, c.Collapsed.Origin, c.Exploded.Origin);
            var expected = ViewGeometry.Project(t, c.ExplodedBox);
            images.Add(new ExplodedImage { Name = c.Name, Moved = moved, Expected = expected, Drawn = c.Drawn });
            if (failure != null || c.Drawn == null) continue;
            if (!ViewGeometry.Within(c.Drawn, expected))
            {
                failure = ("exploded_image", c.Name, $"inside {expected}, its box exploded as drawn", c.Drawn.ToString());
                continue;
            }
            if (c.CollapsedBox != null && Length(moved) > ViewGeometry.PositionToleranceMm)
            {
                var collapsed = ViewGeometry.Project(t, c.CollapsedBox);
                if (ViewGeometry.Within(c.Drawn, collapsed))
                    failure = ("exploded_moved", c.Name, $"moved {moved} from {collapsed}, its box collapsed as drawn", $"{c.Drawn}, still inside it: the view is not shown exploded");
            }
        }
        if (failure == null && images.All(i => i.Drawn == null))
            failure = ("exploded_image", images.Count == 0 ? "" : string.Join(", ", images.Select(i => i.Name)), "edges drawn for at least one component", "none");
        return (images, failure);
    }

    /// <summary>
    /// The box around what a view draws for each component, sheet mm, from IView.GetPolylines7's numbers (read as
    /// ViewGeometry.DrawnPoints reads GetPolylines5's, one polyline per edge) and the component each edge belongs to, in
    /// the same order (<paramref name="owners"/>; null for an edge of no component). FormatException when there are not
    /// as many polylines as edges.
    /// </summary>
    public static Dictionary<string, SheetBox> PerComponent(IReadOnlyList<string?> owners, IReadOnlyList<double> polylines, SheetPoint position, double scale)
    {
        var lines = ViewGeometry.DrawnPolylines(polylines, position, scale);
        if (lines.Count != owners.Count)
            throw new FormatException(string.Format(CultureInfo.InvariantCulture, "SOLIDWORKS gave {0} edges and {1} polylines", owners.Count, lines.Count));
        var boxes = new Dictionary<string, SheetBox>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            if (owners[i] == null || lines[i].Count == 0) continue;
            var box = Around(lines[i]);
            boxes[owners[i]!] = boxes.TryGetValue(owners[i]!, out var had) ? Union(had, box) : box;
        }
        return boxes;
    }

    static double Length(SheetPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    static SheetBox Around(IReadOnlyList<SheetPoint> points) => new SheetBox
    {
        Min = new SheetPoint(points.Min(p => p.X), points.Min(p => p.Y)),
        Max = new SheetPoint(points.Max(p => p.X), points.Max(p => p.Y)),
    };

    static SheetBox Union(SheetBox a, SheetBox b) => new SheetBox
    {
        Min = new SheetPoint(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y)),
        Max = new SheetPoint(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y)),
    };
}
