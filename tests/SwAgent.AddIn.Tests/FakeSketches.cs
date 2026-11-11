using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>A sketch of a <see cref="FakeDocument"/>: what was drawn, related and dimensioned, as SOLIDWORKS would read it back.</summary>
sealed class FakeSketch
{
    public FakeSketch(FakeDocument doc, FakeEntity feature, object on)
    {
        Doc = doc;
        Feature = feature;
        On = on;
    }

    public FakeDocument Doc { get; }
    public FakeEntity Feature { get; }

    /// <summary>The plane or face it was opened on.</summary>
    public object On { get; }

    public string Name => Feature.Info.Name!;

    public List<SketchSegmentRecord> Segments { get; } = new List<SketchSegmentRecord>();
    public List<SketchPointRecord> Points { get; } = new List<SketchPointRecord>();
    public List<(SketchRelationType Type, IReadOnlyList<object> Entities, object Token)> Relations { get; } = new List<(SketchRelationType, IReadOnlyList<object>, object)>();

    /// <summary>How many lines and arcs were ever drawn, for SOLIDWORKS-like names (Line5, Arc2).</summary>
    public int LinesDrawn { get; set; }
    public int ArcsDrawn { get; set; }
    public List<FakeDimension> Dimensions { get; } = new List<FakeDimension>();

    /// <summary>What the solver says; under defined unless a test says otherwise.</summary>
    public SolveState Solve { get; set; } = SolveState.UnderDefined;

    /// <summary>Where SOLIDWORKS put a sketch it made itself (an edge flange's): its origin and axes in model space. Null on a plane or face.</summary>
    public (Point3 Origin, Vector3 X, Vector3 Y, Vector3 Normal)? Frame { get; set; }

    Point3 Origin => Frame?.Origin ?? new Point3(0, 0, 0);

    /// <summary>The sketch's axes as SOLIDWORKS sets them on the standard planes: on top, y runs along -z; on right, x runs along -z.</summary>
    (Vector3 X, Vector3 Y, Vector3 Normal) Axes()
    {
        if (Frame is { } frame) return (frame.X, frame.Y, frame.Normal);
        var n = (On as FakeEntity)?.Info.Normal ?? new Vector3(0, 0, 1);
        if (Math.Abs(n.Y) > 0.999) return (new Vector3(1, 0, 0), new Vector3(0, 0, -1), n);
        if (Math.Abs(n.X) > 0.999) return (new Vector3(0, 0, -1), new Vector3(0, 1, 0), n);
        return (new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1));
    }

    /// <summary>A model point in the sketch's coordinates: its components along the sketch's axes (the fake's origin is the part's).</summary>
    public Point2 ToSketch(Point3 p)
    {
        var (x, y, _) = Axes();
        var o = Origin;
        double dx = p.X - o.X, dy = p.Y - o.Y, dz = p.Z - o.Z;
        return new Point2(dx * x.X + dy * x.Y + dz * x.Z, dx * y.X + dy * y.Y + dz * y.Z);
    }

    public SketchContents Read()
    {
        var (x, y, normal) = Axes();
        var c = new SketchContents
        {
            Name = Name,
            Feature = Feature,
            Solve = Solve,
            Origin = Origin,
            XAxis = x,
            YAxis = y,
            Normal = normal,
        };
        c.Segments.AddRange(Segments);
        c.Points.AddRange(Points);
        c.Relations.AddRange(Relations.Select(r => new SketchRelationRecord(SolidWorksCodes.Constraint(r.Type), r.Entities)));
        c.Dimensions.AddRange(Dimensions.Select(d => new DimensionRecord(d.Entity, d.FullName, d.Value, d.Kind == DimensionKind.Angle, d.Driving, Name)));
        if (Doc.SketchChangesAfterRead) Segments.Add(new SketchSegmentRecord(new object(), SketchSegmentKind.Line, "Line99", false));
        return c;
    }
}

sealed class FakeDimension
{
    public FakeDimension(FakeEntity entity, string fullName, DimensionKind kind, double value)
    {
        Entity = entity;
        FullName = fullName;
        Kind = kind;
        Value = value;
    }

    public FakeEntity Entity { get; }
    public string FullName { get; }
    public DimensionKind Kind { get; }
    public double Value { get; set; }
    public bool Driving { get; set; } = true;

    /// <summary>The feature a feature's own dimension belongs to; null for a sketch's.</summary>
    public string? Feature { get; set; }
}

/// <summary>Draws into a <see cref="FakeSketch"/>; every segment and point is a <see cref="FakeEntity"/> of the document, so references to it work.</summary>
sealed class FakeSketchEditor : ISketchEditor
{
    readonly FakeSketch _sketch;
    readonly Dictionary<(object, string), FakeEntity> _points = new Dictionary<(object, string), FakeEntity>();

    public FakeSketchEditor(FakeSketch sketch)
    {
        _sketch = sketch;
    }

    public object Sketch => _sketch.Feature;

    public string Name => _sketch.Name;

    public bool Closed { get; private set; }

    public Point2 ToSketch(Point3 model) => _sketch.ToSketch(model);

    public void Rename(string name)
    {
        if (_sketch.Doc.RenameFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not rename the sketch");
        _sketch.Doc.RenameFeature(_sketch.Feature, name);
    }

    FakeEntity Segment(SketchSegmentKind kind, string name, bool construction, Action<SketchSegmentRecord> shape)
    {
        var geometry = kind == SketchSegmentKind.Line ? GeometryType.Line : kind == SketchSegmentKind.Spline ? GeometryType.Spline : GeometryType.Circle;
        var e = _sketch.Doc.Add(ReferenceKind.SketchEntity, new EntityInfo { Name = name, Feature = _sketch.Name, Geometry = geometry });
        var record = new SketchSegmentRecord(e, kind, name, construction);
        shape(record);
        if (!(kind == SketchSegmentKind.Circle && _sketch.Doc.SketchLosesCircles)) _sketch.Segments.Add(record);
        return e;
    }

    public object Line(Point2 start, Point2 end, bool construction) =>
        Segment(SketchSegmentKind.Line, "Line" + ++_sketch.LinesDrawn, construction, r =>
        {
            r.Start = start;
            r.End = end;
            r.LengthMm = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        });

    public object Arc(Point2 center, Point2 start, Point2 end, bool counterclockwise) =>
        Segment(SketchSegmentKind.Arc, "Arc" + ++_sketch.ArcsDrawn, false, r =>
        {
            r.Center = center;
            r.Start = start;
            r.End = end;
            r.RadiusMm = Math.Sqrt(Math.Pow(start.X - center.X, 2) + Math.Pow(start.Y - center.Y, 2));
        });

    public object Circle(Point2 center, double radiusMm) =>
        Segment(SketchSegmentKind.Circle, "Arc" + ++_sketch.ArcsDrawn, false, r =>
        {
            r.Center = center;
            r.RadiusMm = radiusMm;
        });

    /// <summary>Top, left, bottom, right: not the order the add-in names them in.</summary>
    public IReadOnlyList<object> Rectangle(Point2 a, Point2 b)
    {
        double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        return new[]
        {
            Line(new Point2(x1, y1), new Point2(x0, y1), false),
            Line(new Point2(x0, y1), new Point2(x0, y0), false),
            Line(new Point2(x0, y0), new Point2(x1, y0), false),
            Line(new Point2(x1, y0), new Point2(x1, y1), false),
        };
    }

    /// <summary>The sides from the last back to the first, so the add-in must order them.</summary>
    public IReadOnlyList<object> Polygon(Point2 center, Point2 vertex, int sides)
    {
        double r = Math.Sqrt(Math.Pow(vertex.X - center.X, 2) + Math.Pow(vertex.Y - center.Y, 2));
        double start = Math.Atan2(vertex.Y - center.Y, vertex.X - center.X);
        Point2 Corner(int i) => new Point2(center.X + r * Math.Cos(start + 2 * Math.PI * i / sides), center.Y + r * Math.Sin(start + 2 * Math.PI * i / sides));
        var drawn = Enumerable.Range(0, sides).Select(i => Line(Corner(i), Corner(i + 1), false)).ToList();
        drawn.Reverse();
        return drawn;
    }

    public IReadOnlyList<object> Slot(Point2 start, Point2 end, double widthMm)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double nx = -dy / length * widthMm / 2, ny = dx / length * widthMm / 2;
        return new[]
        {
            Line(new Point2(start.X + nx, start.Y + ny), new Point2(end.X + nx, end.Y + ny), false),
            Line(new Point2(start.X - nx, start.Y - ny), new Point2(end.X - nx, end.Y - ny), false),
            Arc(start, new Point2(start.X - nx, start.Y - ny), new Point2(start.X + nx, start.Y + ny), true),
            Arc(end, new Point2(end.X + nx, end.Y + ny), new Point2(end.X - nx, end.Y - ny), true),
        };
    }

    public object Spline(IReadOnlyList<Point2> points) =>
        Segment(SketchSegmentKind.Spline, "Spline" + ++_sketch.LinesDrawn, false, r =>
        {
            r.Start = points[0];
            r.End = points[points.Count - 1];
        });

    public object Point(Point2 at)
    {
        var e = _sketch.Doc.Add(ReferenceKind.SketchPoint, new EntityInfo { Feature = _sketch.Name, Geometry = GeometryType.Point });
        _sketch.Points.Add(new SketchPointRecord(e, at));
        return e;
    }

    public (Point2 Start, Point2 End) Ends(object line)
    {
        var r = _sketch.Segments.First(s => ReferenceEquals(s.Entity, line));
        return (r.Start!, r.End!);
    }

    public object PointOf(object segment, string part)
    {
        if (!_points.TryGetValue((segment, part), out var point))
        {
            point = _sketch.Doc.Add(ReferenceKind.SketchPoint, new EntityInfo { Feature = _sketch.Name, Geometry = GeometryType.Point, Name = $"{((FakeEntity)segment).Info.Name}.{part}" });
            _points[(segment, part)] = point;
        }
        return point;
    }

    public object? Relate(SketchRelationType type, IReadOnlyList<object> entities)
    {
        if (_sketch.Doc.SketchRefuses == type) return null;
        if (_sketch.Doc.SketchRefusesSaying == type) throw new SwAgentException(ErrorCode.OperationFailed, "SOLIDWORKS selected 1 of 2");
        var token = new object();
        _sketch.Relations.Add((type, entities.ToList(), token));
        return token;
    }

    public void DeleteRelation(object relation) => _sketch.Relations.RemoveAll(r => ReferenceEquals(r.Token, relation));

    public void DeleteDimension(string fullName) => _sketch.Dimensions.RemoveAll(d => d.FullName == fullName);

    public string? Dimension(DimensionKind kind, IReadOnlyList<object> entities, double value, string? name)
    {
        if (_sketch.Doc.SketchRefusesDimensions) return null;
        string full = $"{name ?? "D" + (_sketch.Dimensions.Count + 1)}@{_sketch.Name}";
        var e = _sketch.Doc.Add(ReferenceKind.Dimension, new EntityInfo { Name = full, Geometry = GeometryType.Other });
        _sketch.Dimensions.Add(new FakeDimension(e, full, kind, value + _sketch.Doc.SketchDimensionError) { Driving = !_sketch.Doc.SketchDimensionsDriven });
        if (_sketch.Doc.SketchOverDefines) _sketch.Solve = SolveState.OverDefined;
        return full;
    }

    public void Delete(object entity)
    {
        _sketch.Segments.RemoveAll(s => ReferenceEquals(s.Entity, entity));
        _sketch.Points.RemoveAll(p => ReferenceEquals(p.Entity, entity));
        _sketch.Doc.Entities.Remove((FakeEntity)entity);
    }

    public void Close() => Closed = true;
}
