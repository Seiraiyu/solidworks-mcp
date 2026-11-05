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

public enum SketchSegmentKind
{
    Line,
    Arc,
    Circle,
    Ellipse,
    Spline,
    Other,
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

[Operation("sketch.create", typeof(SketchCreateResult))]
[Description("Draws a whole sketch in one call on a plane or a planar face: its lines, centerlines, arcs, circles, rectangles, polygons, slots, splines and points, then its relations, then its named driving dimensions; then closes it and checks it. Coordinates are mm in the sketch's own plane; the result gives the sketch's origin and axes in the part. SOLIDWORKS is never left editing the sketch. Checked: the sketch holds what was asked, every dimension reads back at its value, and it is not over defined; otherwise nothing is left of it (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SketchCreateRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("What to sketch on: front, top or right (the template's own planes), a plane's name or reference, or a planar face's reference exactly as a result gave it.")]
    public string On { get; set; } = "";

    [Description("The sketch's name; SOLIDWORKS names it (Sketch1, Sketch2 …) when absent. It must not already be a feature's name.")]
    public string? Name { get; set; }

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

    /// <summary>The request's entity, relation and dimension lists, as the sketch check reads them.</summary>
    public SketchGeometry Geometry() => new SketchGeometry
    {
        Lines = Lines,
        CenterLines = CenterLines,
        Arcs = Arcs,
        Circles = Circles,
        Rectangles = Rectangles,
        Polygons = Polygons,
        Slots = Slots,
        Splines = Splines,
        Points = Points,
        Relations = Relations,
        Dimensions = Dimensions,
    };
}

[Description("A sketch made or changed, with references to what was drawn.")]
public sealed class SketchCreateResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: what was made, and the part after it.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The sketch.")]
    public SketchSummary Sketch { get; set; } = new SketchSummary();

    [Required]
    [Description("Each id in the request, with the references of what it drew.")]
    public List<SketchIdReferences> Ids { get; set; } = new List<SketchIdReferences>();

    [Required]
    [Description("The dimensions the request added, in its order.")]
    public List<DimensionInfo> Dimensions { get; set; } = new List<DimensionInfo>();
}

[Description("A sketch: its name, reference, how far it is defined, and where its plane is in the part.")]
public sealed class SketchSummary
{
    [Required]
    [Description("Its name, for example Sketch3.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("fully_defined, under_defined (allowed), over_defined, no_solution or unknown.")]
    public SolveState Solve { get; set; }

    [Required]
    [Description("The sketch's origin in the part, mm.")]
    public Point3 Origin { get; set; } = new Point3();

    [Required]
    [Description("The sketch's x axis in the part.")]
    public Vector3 XAxis { get; set; } = new Vector3();

    [Required]
    [Description("The sketch's y axis in the part.")]
    public Vector3 YAxis { get; set; } = new Vector3();

    [Required]
    [Description("The sketch plane's normal in the part (x axis × y axis).")]
    public Vector3 Normal { get; set; } = new Vector3();
}

[Description("An id from the request and the references of what it drew.")]
public sealed class SketchIdReferences
{
    [Required]
    [Description("The id.")]
    public string Id { get; set; } = "";

    [Required]
    [Description("One reference for a line, centerline, arc, circle, spline or point; four for a rectangle (bottom, right, top, left); one per side for a polygon, side1 first; four for a slot. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public List<string> References { get; set; } = new List<string>();
}

[Description("A dimension.")]
public sealed class DimensionInfo
{
    [Required]
    [Description("Its full name, for example width@Sketch3 or D1@Boss-Extrude1; dimension.set takes it.")]
    public string Name { get; set; } = "";

    [Description("Its value: mm, or degrees when unit is deg.")]
    public double Value { get; set; }

    [Required]
    [Description("mm or deg.")]
    public string Unit { get; set; } = "mm";

    [Description("True when it drives the geometry; false when it only reports it (driven).")]
    public bool Driving { get; set; }

    [Description("The feature or sketch it belongs to.")]
    public string? Feature { get; set; }

    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }
}

[Operation("sketch.read", typeof(SketchReadResult), ReadOnly = true)]
[Description("Reads a sketch back: every segment (its kind, geometry in mm in the sketch, whether it is construction geometry, and its reference), every sketch point (the ends and centres of segments included), every relation (its type and what it relates), every dimension (full name, value, driving or driven), how far it is defined, and where its plane is in the part. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SketchReadRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch: its name (Sketch3) or its reference exactly as a result gave it.")]
    public string Sketch { get; set; } = "";
}

[Description("A sketch, read back.")]
public sealed class SketchReadResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The sketch.")]
    public SketchSummary Sketch { get; set; } = new SketchSummary();

    [Required]
    [Description("Every segment, in SOLIDWORKS' order.")]
    public List<SketchSegmentInfo> Segments { get; set; } = new List<SketchSegmentInfo>();

    [Required]
    [Description("Every sketch point, the ends and centres of segments included.")]
    public List<SketchPointInfo> Points { get; set; } = new List<SketchPointInfo>();

    [Required]
    [Description("Every relation.")]
    public List<SketchRelationInfo> Relations { get; set; } = new List<SketchRelationInfo>();

    [Required]
    [Description("Every dimension.")]
    public List<DimensionInfo> Dimensions { get; set; } = new List<DimensionInfo>();
}

[Description("A sketch segment.")]
public sealed class SketchSegmentInfo
{
    [Description("line, arc, circle, ellipse, spline or other.")]
    public SketchSegmentKind Kind { get; set; }

    [Required]
    [Description("SOLIDWORKS' name for it, for example Line1.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("True for construction geometry (centerlines included).")]
    public bool Construction { get; set; }

    [Description("Where a line, arc or spline starts, mm in the sketch.")]
    public Point2? Start { get; set; }

    [Description("Where a line, arc or spline ends, mm in the sketch.")]
    public Point2? End { get; set; }

    [Description("The centre of an arc or circle, mm in the sketch.")]
    public Point2? Center { get; set; }

    [Description("The radius of an arc or circle, mm.")]
    public double? RadiusMm { get; set; }

    [Description("Its length, mm.")]
    public double LengthMm { get; set; }
}

[Description("A sketch point.")]
public sealed class SketchPointInfo
{
    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Required]
    [Description("Where it is, mm in the sketch.")]
    public Point2 At { get; set; } = new Point2();
}

[Description("A sketch relation.")]
public sealed class SketchRelationInfo
{
    [Required]
    [Description("Its type: one of the relation types sketch.create takes (horizontal, coincident, equal …), or SOLIDWORKS' own name for any other (distance and radius are a dimension's own).")]
    public string Type { get; set; } = "";

    [Required]
    [Description("What it relates: references, or a name where SOLIDWORKS gives no reference (origin for the sketch's origin).")]
    public List<string> Entities { get; set; } = new List<string>();
}

[Operation("sketch.modify", typeof(SketchCreateResult))]
[Description("Changes an existing sketch: draws, relates and dimensions what add says (as sketch.create does; add's relations and dimensions may also name the sketch's own entities by reference), checks the sketch, and only then removes what remove names. Checked as sketch.create is; additions that fail are taken back. A removed entity cannot be drawn back with the same reference, so a failure after removing answers operation_failed with rollback_failed. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SketchModifyRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch: its name (Sketch3) or its reference exactly as a result gave it.")]
    public string Sketch { get; set; } = "";

    [Description("What to draw, relate and dimension, as in sketch.create; fields are reported under /add.")]
    public SketchGeometry? Add { get; set; }

    [Description("References of the sketch's own entities and points to delete, exactly as a result gave them; relations and dimensions on them go with them.")]
    public List<string>? Remove { get; set; }
}