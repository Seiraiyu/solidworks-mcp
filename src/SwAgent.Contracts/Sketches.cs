using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

public enum ArcDirection
{
    Counterclockwise,
    Clockwise,
}

/// <summary>SOLIDWORKS' sketch relations an agent can add (design 371, Sketches).</summary>
public enum SketchRelationType
{
    Horizontal,
    Vertical,
    Coincident,
    Tangent,
    Parallel,
    Perpendicular,
    Equal,
    Concentric,
    Midpoint,
    Collinear,
    Symmetric,
    Fix,
}

public enum DimensionKind
{
    Linear,
    Horizontal,
    Vertical,
    Radius,
    Diameter,
    Angle,
}

/// <summary>How far SOLIDWORKS' sketch solver says a sketch is defined (swConstrainedStatus_e).</summary>
public enum SolveState
{
    FullyDefined,
    UnderDefined,
    OverDefined,
    NoSolution,
    Unknown,
}

[Description("A line from start to end.")]
public sealed class SketchLine
{
    [Required]
    [Description("Your own short name for it, unique in the request: a letter, then letters, digits or _. Relations and dimensions name it, with .start or .end for its ends.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Where it starts, mm in the sketch.")]
    public Point2 Start { get; set; } = new Point2();

    [Required]
    [Description("Where it ends, mm in the sketch.")]
    public Point2 End { get; set; } = new Point2();

    [Description("A construction line (it shapes the sketch but makes no geometry). Default false.")]
    public bool? Construction { get; set; }
}

[Description("A centerline: a construction line, for example the axis of a revolve or of a symmetric relation.")]
public sealed class SketchCenterLine
{
    [Required]
    [Description("Your own short name for it, unique in the request; .start and .end name its ends.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Where it starts, mm in the sketch.")]
    public Point2 Start { get; set; } = new Point2();

    [Required]
    [Description("Where it ends, mm in the sketch.")]
    public Point2 End { get; set; } = new Point2();
}

[Description("A circular arc about a centre, from start to end; start and end must be the same distance from the centre.")]
public sealed class SketchArc
{
    [Required]
    [Description("Your own short name for it, unique in the request; .center, .start and .end name its points.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Its centre, mm in the sketch.")]
    public Point2 Center { get; set; } = new Point2();

    [Required]
    [Description("Where it starts, mm in the sketch.")]
    public Point2 Start { get; set; } = new Point2();

    [Required]
    [Description("Where it ends, mm in the sketch.")]
    public Point2 End { get; set; } = new Point2();

    [Description("Which way it runs from start to end, seen from the sketch's front. Default counterclockwise.")]
    public ArcDirection? Direction { get; set; }
}

[Description("A circle.")]
public sealed class SketchCircle
{
    [Required]
    [Description("Your own short name for it, unique in the request; .center names its centre.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Its centre, mm in the sketch.")]
    public Point2 Center { get; set; } = new Point2();

    [Description("Its radius, mm; more than 0.")]
    public double RadiusMm { get; set; }
}

[Description("A rectangle with sides along the sketch's x and y axes: four lines, named .bottom, .right, .top and .left.")]
public sealed class SketchRectangle
{
    [Required]
    [Description("Your own short name for it, unique in the request; its sides are .bottom, .right, .top and .left.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("One corner, mm in the sketch; its centre when centered.")]
    public Point2 Corner1 { get; set; } = new Point2();

    [Required]
    [Description("The opposite corner, mm in the sketch.")]
    public Point2 Corner2 { get; set; } = new Point2();

    [Description("Corner1 is the rectangle's centre. Default false.")]
    public bool? Centered { get; set; }
}

[Description("A regular polygon: lines named .side1, .side2 … in order from the vertex, counterclockwise.")]
public sealed class SketchPolygon
{
    [Required]
    [Description("Your own short name for it, unique in the request; its sides are .side1, .side2 ….")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Its centre, mm in the sketch.")]
    public Point2 Center { get; set; } = new Point2();

    [Required]
    [Description("One of its vertices, mm in the sketch.")]
    public Point2 Vertex { get; set; } = new Point2();

    [Description("How many sides, 3 to 64.")]
    public int Sides { get; set; }
}

[Description("A straight slot: two straight sides and two half-circle ends, around the line from start to end.")]
public sealed class SketchSlot
{
    [Required]
    [Description("Your own short name for it, unique in the request.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("The centre of one end, mm in the sketch.")]
    public Point2 Start { get; set; } = new Point2();

    [Required]
    [Description("The centre of the other end, mm in the sketch.")]
    public Point2 End { get; set; } = new Point2();

    [Description("Its width (the ends' diameter), mm; more than 0.")]
    public double WidthMm { get; set; }
}

[Description("A spline through points, in order.")]
public sealed class SketchSpline
{
    [Required]
    [Description("Your own short name for it, unique in the request.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("The points it passes through, mm in the sketch: at least 2, no two in a row the same.")]
    public List<Point2> Points { get; set; } = new List<Point2>();
}

[Description("A sketch point.")]
public sealed class SketchPointItem
{
    [Required]
    [Description("Your own short name for it, unique in the request.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("Where it is, mm in the sketch.")]
    public Point2 At { get; set; } = new Point2();
}

[Description("A relation between sketch entities, or between them and the part's edges and vertices.")]
public sealed class SketchRelationItem
{
    [Description("horizontal or vertical (one line, or two points), coincident, tangent, concentric, perpendicular (two), parallel, equal, collinear (two or more), midpoint (a point, then a line), symmetric (two entities, then a centerline), fix (one or more).")]
    public SketchRelationType Type { get; set; }

    [Required]
    [Description("What it relates: your ids (with .start, .end, .center or a side such as .bottom for a point or a side), origin for the sketch's origin, or references to the part's edges and vertices exactly as a result gave them.")]
    public List<string> Entities { get; set; } = new List<string>();
}

[Description("A driving dimension.")]
public sealed class SketchDimensionItem
{
    [Description("Its name, so it reads name@Sketch3: a letter, then letters, digits or _. SOLIDWORKS names it (D1, D2 …) when absent.")]
    public string? Name { get; set; }

    [Required]
    [Description("What it measures, named as in relations: one line (its length), one circle or arc (diameter or radius), or two entities (the distance or angle between them).")]
    public List<string> Entities { get; set; } = new List<string>();

    [Description("Its value: mm, or degrees for an angle; more than 0 (an angle less than 360).")]
    public double Value { get; set; }

    [Description("linear, horizontal, vertical, radius, diameter or angle. Default: diameter for a circle, radius for an arc, linear otherwise.")]
    public DimensionKind? Kind { get; set; }
}

[Description("What to draw in a sketch, and how to constrain it.")]
public sealed class SketchGeometry
{
    [Description("Lines.")]
    public List<SketchLine>? Lines { get; set; }

    [Description("Centerlines (construction).")]
    public List<SketchCenterLine>? CenterLines { get; set; }

    [Description("Arcs.")]
    public List<SketchArc>? Arcs { get; set; }

    [Description("Circles.")]
    public List<SketchCircle>? Circles { get; set; }

    [Description("Rectangles.")]
    public List<SketchRectangle>? Rectangles { get; set; }

    [Description("Regular polygons.")]
    public List<SketchPolygon>? Polygons { get; set; }

    [Description("Straight slots.")]
    public List<SketchSlot>? Slots { get; set; }

    [Description("Splines.")]
    public List<SketchSpline>? Splines { get; set; }

    [Description("Sketch points.")]
    public List<SketchPointItem>? Points { get; set; }

    [Description("Relations, added after everything is drawn.")]
    public List<SketchRelationItem>? Relations { get; set; }

    [Description("Driving dimensions, added after the relations.")]
    public List<SketchDimensionItem>? Dimensions { get; set; }
}
