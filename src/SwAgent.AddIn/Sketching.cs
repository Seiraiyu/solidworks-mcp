using System.Collections.Generic;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// A sketch open for drawing, as the sketch operations draw it, so they can be tested without SOLIDWORKS. UI thread only.
/// Coordinates are mm in the sketch's own plane, angles degrees. Entities are SOLIDWORKS' own objects (sketch segments
/// and points), only ever handed back to the same editor or document. Close always ends sketch editing.
/// </summary>
public interface ISketchEditor
{
    /// <summary>The sketch feature being edited.</summary>
    object Sketch { get; }

    /// <summary>The sketch's name now, for example Sketch3.</summary>
    string Name { get; }

    void Rename(string name);

    object Line(Point2 start, Point2 end, bool construction);

    object Arc(Point2 center, Point2 start, Point2 end, bool counterclockwise);

    object Circle(Point2 center, double radiusMm);

    /// <summary>A rectangle with sides along the sketch's axes: its four lines, in no particular order.</summary>
    IReadOnlyList<object> Rectangle(Point2 corner1, Point2 corner2);

    /// <summary>A regular polygon: its sides, in no particular order (any construction circle SOLIDWORKS adds is not among them).</summary>
    IReadOnlyList<object> Polygon(Point2 center, Point2 vertex, int sides);

    /// <summary>A straight slot: its two sides and two ends, in no particular order.</summary>
    IReadOnlyList<object> Slot(Point2 start, Point2 end, double widthMm);

    object Spline(IReadOnlyList<Point2> points);

    object Point(Point2 at);

    /// <summary>A line's start and end, mm in the sketch.</summary>
    (Point2 Start, Point2 End) Ends(object line);

    /// <summary>The sketch point at a segment's start, end or center.</summary>
    object PointOf(object segment, string part);

    /// <summary>Adds a relation between sketch entities, the origin (<see cref="SketchOrigin"/>) or the part's edges and vertices: the relation, or null when SOLIDWORKS did not add it.</summary>
    object? Relate(SketchRelationType type, IReadOnlyList<object> entities);

    /// <summary>Deletes a relation <see cref="Relate"/> added.</summary>
    void DeleteRelation(object relation);

    /// <summary>Adds a driving dimension of <paramref name="kind"/>, names it and sets it (mm, or degrees); its full name, or null when SOLIDWORKS added none.</summary>
    string? Dimension(DimensionKind kind, IReadOnlyList<object> entities, double value, string? name);

    /// <summary>Deletes a dimension by its full name.</summary>
    void DeleteDimension(string fullName);

    /// <summary>Deletes a sketch entity.</summary>
    void Delete(object entity);

    /// <summary>Ends sketch editing; safe to call more than once.</summary>
    void Close();
}

/// <summary>Names the sketch's origin (the part's origin seen in the sketch) as a relation or dimension target.</summary>
public sealed class SketchOrigin
{
    public static readonly SketchOrigin Instance = new SketchOrigin();

    SketchOrigin()
    {
    }
}

/// <summary>Everything in a sketch, read back.</summary>
public sealed class SketchContents
{
    public string Name { get; set; } = "";

    /// <summary>The sketch feature.</summary>
    public object Feature { get; set; } = new object();

    public SolveState Solve { get; set; }

    /// <summary>The sketch's origin and axes in the part's model space.</summary>
    public Point3 Origin { get; set; } = new Point3();

    public Vector3 XAxis { get; set; } = new Vector3(1, 0, 0);
    public Vector3 YAxis { get; set; } = new Vector3(0, 1, 0);
    public Vector3 Normal { get; set; } = new Vector3(0, 0, 1);

    public List<SketchSegmentRecord> Segments { get; } = new List<SketchSegmentRecord>();
    public List<SketchPointRecord> Points { get; } = new List<SketchPointRecord>();
    public List<SketchRelationRecord> Relations { get; } = new List<SketchRelationRecord>();
    public List<DimensionRecord> Dimensions { get; } = new List<DimensionRecord>();
}

/// <summary>A sketch segment: its kind, geometry in mm in the sketch, and whether it is construction geometry.</summary>
public sealed class SketchSegmentRecord
{
    public SketchSegmentRecord(object entity, SketchSegmentKind kind, string name, bool construction)
    {
        Entity = entity;
        Kind = kind;
        Name = name;
        Construction = construction;
    }

    public object Entity { get; }
    public SketchSegmentKind Kind { get; }
    public string Name { get; }
    public bool Construction { get; }
    public Point2? Start { get; set; }
    public Point2? End { get; set; }
    public Point2? Center { get; set; }
    public double? RadiusMm { get; set; }
    public double LengthMm { get; set; }
}

public sealed class SketchPointRecord
{
    public SketchPointRecord(object entity, Point2 at)
    {
        Entity = entity;
        At = at;
    }

    public object Entity { get; }
    public Point2 At { get; }
}

/// <summary>A relation: SOLIDWORKS' type for it (swConstraintType_e) and what it relates.</summary>
public sealed class SketchRelationRecord
{
    public SketchRelationRecord(int type, IReadOnlyList<object> entities)
    {
        Type = type;
        Entities = entities;
    }

    public int Type { get; }
    public IReadOnlyList<object> Entities { get; }
}

/// <summary>A dimension: its full name, value (mm, or degrees for an angle), whether it drives, and the feature or sketch it belongs to.</summary>
public sealed class DimensionRecord
{
    public DimensionRecord(object dimension, string fullName, double value, bool angle, bool driving, string feature)
    {
        Dimension = dimension;
        FullName = fullName;
        Value = value;
        Angle = angle;
        Driving = driving;
        Feature = feature;
    }

    /// <summary>SOLIDWORKS' dimension object, for references and for setting it.</summary>
    public object Dimension { get; }
    public string FullName { get; }
    public double Value { get; }
    public bool Angle { get; }
    public bool Driving { get; }
    public string Feature { get; }
}
