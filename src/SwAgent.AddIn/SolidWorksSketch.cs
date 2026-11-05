using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// A sketch open for drawing, through SOLIDWORKS' ISketchManager. UI thread only. Opened with the sketch's own plane
/// selected (InsertSketch), drawn with AddToDB on (no inference, no snapping: exactly the coordinates asked for) and
/// DisplayWhenAdded off, and closed by toggling InsertSketch again. Coordinates in an active 2D sketch are the sketch's
/// own, so millimetres become metres here and nowhere else. Covered by the parts live suite, not by unit tests.
/// </summary>
internal sealed class SolidWorksSketchEditor : ISketchEditor
{
    const double M = 1000.0;

    readonly IModelDoc2 _doc;
    readonly ISketchManager _manager;
    readonly bool _addToDb;
    readonly bool _display;
    bool _closed;

    SolidWorksSketchEditor(IModelDoc2 doc, IFeature sketch)
    {
        _doc = doc;
        _manager = doc.SketchManager;
        Feature = sketch;
        _addToDb = _manager.AddToDB;
        _display = _manager.DisplayWhenAdded;
        _manager.AddToDB = true;
        _manager.DisplayWhenAdded = false;
    }

    public IFeature Feature { get; }

    public object Sketch => Feature;

    public string Name => Feature.Name;

    ISketch Active => (ISketch)Feature.GetSpecificFeature2();

    /// <summary>Opens a new sketch on <paramref name="planeOrFace"/> (a plane feature or a planar face).</summary>
    public static SolidWorksSketchEditor Open(IModelDoc2 doc, object planeOrFace)
    {
        doc.ClearSelection2(true);
        bool selected = planeOrFace switch
        {
            IFeature plane => plane.Select2(false, 0),
            IEntity face => face.Select4(false, null),
            _ => false,
        };
        if (!selected) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the plane or face to sketch on");
        doc.SketchManager.InsertSketch(true);
        if (!(doc.SketchManager.ActiveSketch is ISketch sketch) || !(sketch is IFeature feature))
            throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not open a sketch on the plane or face");
        doc.ClearSelection2(true);
        return new SolidWorksSketchEditor(doc, feature);
    }

    public void Rename(string name)
    {
        Feature.Name = name;
        if (Feature.Name != name) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not rename {Feature.Name} to {name}");
    }

    public object Line(Point2 start, Point2 end, bool construction)
    {
        var line = construction
            ? _manager.CreateCenterLine(start.X / M, start.Y / M, 0, end.X / M, end.Y / M, 0)
            : _manager.CreateLine(start.X / M, start.Y / M, 0, end.X / M, end.Y / M, 0);
        return line ?? throw Refused("line");
    }

    public object Arc(Point2 center, Point2 start, Point2 end, bool counterclockwise) =>
        (object?)_manager.CreateArc(center.X / M, center.Y / M, 0, start.X / M, start.Y / M, 0, end.X / M, end.Y / M, 0, (short)(counterclockwise ? 1 : -1))
        ?? throw Refused("arc");

    public object Circle(Point2 center, double radiusMm) =>
        (object?)_manager.CreateCircleByRadius(center.X / M, center.Y / M, 0, radiusMm / M) ?? throw Refused("circle");

    public IReadOnlyList<object> Rectangle(Point2 corner1, Point2 corner2) =>
        Segments(_manager.CreateCornerRectangle(corner1.X / M, corner1.Y / M, 0, corner2.X / M, corner2.Y / M, 0), "rectangle");

    public IReadOnlyList<object> Polygon(Point2 center, Point2 vertex, int sides) =>
        Drawn(() => _manager.CreatePolygon(center.X / M, center.Y / M, 0, vertex.X / M, vertex.Y / M, 0, sides, true), "polygon");

    public IReadOnlyList<object> Slot(Point2 start, Point2 end, double widthMm) =>
        Drawn(() => _manager.CreateSketchSlot((int)swSketchSlotCreationType_e.swSketchSlotCreationType_line, (int)swSketchSlotLengthType_e.swSketchSlotLengthType_CenterCenter,
            widthMm / M, start.X / M, start.Y / M, 0, end.X / M, end.Y / M, 0, end.X / M, end.Y / M, 0, 1, false), "slot");

    public object Spline(IReadOnlyList<Point2> points)
    {
        var xyz = points.SelectMany(p => new[] { p.X / M, p.Y / M, 0.0 }).ToArray();
        return (object?)_manager.CreateSpline2(xyz, false) ?? throw Refused("spline");
    }

    public object Point(Point2 at) => (object?)_manager.CreatePoint(at.X / M, at.Y / M, 0) ?? throw Refused("point");

    public (Point2 Start, Point2 End) Ends(object line)
    {
        var l = (ISketchLine)line;
        return (Mm((ISketchPoint)l.GetStartPoint2()), Mm((ISketchPoint)l.GetEndPoint2()));
    }

    public object PointOf(object segment, string part)
    {
        object? point = (segment, part) switch
        {
            (ISketchLine l, "start") => l.GetStartPoint2(),
            (ISketchLine l, "end") => l.GetEndPoint2(),
            (ISketchArc a, "start") => a.GetStartPoint2(),
            (ISketchArc a, "end") => a.GetEndPoint2(),
            (ISketchArc a, "center") => a.GetCenterPoint2(),
            _ => null,
        };
        return point ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gave no {part} point for a sketch segment");
    }

    /// <summary>
    /// The relation SOLIDWORKS added, taken as the last in its list (it lists relations in the order they were made), or
    /// null when none was. A coincident relation between points (3.B live run, the plate's line ends): when they are
    /// already one point (SOLIDWORKS joins line ends drawn at one place) it holds as it is and <see cref="Joined"/> stands
    /// for it; otherwise sgCOINCIDENT, then sgMERGEPOINTS (how SOLIDWORKS relates two points), and when both are refused
    /// the refusal says what SOLIDWORKS selected and which points they were.
    /// </summary>
    public object? Relate(SketchRelationType type, IReadOnlyList<object> entities)
    {
        bool points = entities.All(e => e is ISketchPoint || e is IVertex || e is SketchOrigin);
        if (type != SketchRelationType.Coincident || !points) return Constrain(ConstraintName(type, points), entities);
        var sketchPoints = entities.OfType<ISketchPoint>().ToList();
        if (sketchPoints.Count == entities.Count && sketchPoints.Select(PointId).Distinct().Count() == 1) return Joined.Instance;
        var relation = Constrain("sgCOINCIDENT", entities) ?? Constrain("sgMERGEPOINTS", entities);
        if (relation != null) return relation;
        Select(entities);
        int selected = _doc.SelectionManager is ISelectionMgr selection ? selection.GetSelectedObjectCount2(-1) : -1;
        _doc.ClearSelection2(true);
        string described = string.Join("; ", entities.Select(e => e switch
        {
            ISketchPoint p => $"sketch point {PointId(p)} at {Mm(p)}",
            SketchOrigin _ => "the origin",
            _ => e.GetType().Name,
        }));
        throw new SwAgentException(ErrorCode.OperationFailed,
            $"sgCOINCIDENT and sgMERGEPOINTS added no relation (SOLIDWORKS selected {selected} of {entities.Count}: {described})");
    }

    /// <summary>A coincident relation that holds because its points are already one point: there is nothing to take back.</summary>
    internal sealed class Joined
    {
        public static readonly Joined Instance = new Joined();

        Joined() { }
    }

    static string PointId(ISketchPoint p) => string.Join(",", (int[])p.GetID());

    object? Constrain(string constraint, IReadOnlyList<object> entities)
    {
        var relations = Active.RelationManager;
        int before = relations.GetRelationsCount((int)swSketchRelationFilterType_e.swAll);
        Select(entities);
        _doc.SketchAddConstraints(constraint);
        _doc.ClearSelection2(true);
        if (relations.GetRelationsCount((int)swSketchRelationFilterType_e.swAll) <= before) return null;
        return relations.GetRelations((int)swSketchRelationFilterType_e.swAll) is object[] all && all.Length > 0 ? all[all.Length - 1] : null;
    }

    public void DeleteRelation(object relation)
    {
        if (relation is Joined) return;
        if (!Active.RelationManager.DeleteRelation((SketchRelation)relation))
            throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not delete a sketch relation");
    }

    /// <summary>Deletes the sketch's dimension named <paramref name="fullName"/> (name@owner), selected by SOLIDWORKS' own full name, which adds the document.</summary>
    public void DeleteDimension(string fullName)
    {
        string own = fullName;
        for (var d = Feature.GetFirstDisplayDimension() as IDisplayDimension; d != null; d = Feature.GetNextDisplayDimension(d) as IDisplayDimension)
        {
            if (d.GetDimension2(0) is IDimension dimension && string.Equals(SolidWorksCodes.DimensionName(dimension.FullName), fullName, StringComparison.OrdinalIgnoreCase))
            {
                own = dimension.FullName;
                break;
            }
        }
        _doc.ClearSelection2(true);
        bool selected = _doc.Extension.SelectByID2(own, "DIMENSION", 0, 0, 0, false, 0, null, 0);
        bool deleted = selected && _doc.Extension.DeleteSelection2(0);
        _doc.ClearSelection2(true);
        if (!deleted) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not delete the dimension {fullName}");
    }

    public string? Dimension(DimensionKind kind, IReadOnlyList<object> entities, double value, string? name)
    {
        Select(entities);
        var at = TextLocation(entities);
        object? added = kind switch
        {
            DimensionKind.Horizontal => _doc.AddHorizontalDimension2(at[0], at[1], at[2]),
            DimensionKind.Vertical => _doc.AddVerticalDimension2(at[0], at[1], at[2]),
            DimensionKind.Radius => _doc.AddRadialDimension2(at[0], at[1], at[2]),
            DimensionKind.Diameter => _doc.AddDiameterDimension2(at[0], at[1], at[2]),
            _ => _doc.AddDimension2(at[0], at[1], at[2]),
        };
        _doc.ClearSelection2(true);
        if (!(added is IDisplayDimension display) || !(display.GetDimension2(0) is IDimension dimension)) return null;
        if (name != null) dimension.Name = name;
        double system = kind == DimensionKind.Angle ? value * Math.PI / 180 : value / M;
        dimension.SetSystemValue3(system, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
        return SolidWorksCodes.DimensionName(dimension.FullName);
    }

    public void Delete(object entity)
    {
        Select(new[] { entity });
        bool deleted = _doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
        _doc.ClearSelection2(true);
        if (!deleted) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not delete the sketch entity");
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _doc.ClearSelection2(true);
            if (_manager.ActiveSketch != null) _manager.InsertSketch(true);
        }
        finally
        {
            _manager.AddToDB = _addToDb;
            _manager.DisplayWhenAdded = _display;
        }
    }

    /// <summary>Opens an existing sketch for drawing (sketch.modify).</summary>
    public static SolidWorksSketchEditor Edit(IModelDoc2 doc, IFeature sketch)
    {
        doc.ClearSelection2(true);
        if (!sketch.Select2(false, 0)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select {sketch.Name}");
        doc.EditSketch();
        if (doc.SketchManager.ActiveSketch == null) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not open {sketch.Name} for editing");
        doc.ClearSelection2(true);
        return new SolidWorksSketchEditor(doc, sketch);
    }

    void Select(IReadOnlyList<object> entities)
    {
        _doc.ClearSelection2(true);
        foreach (var e in entities)
        {
            bool selected = e switch
            {
                ISketchSegment s => s.Select4(true, null),
                ISketchPoint p => p.Select4(true, null),
                SketchOrigin _ => SelectOrigin(),
                IEntity model => model.Select4(true, null),
                _ => false,
            };
            if (!selected) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select a {e.GetType().Name} for a relation or dimension");
        }
    }

    /// <summary>
    /// Adds the part's origin to the selection: the origin feature (found by its type, never by its name, which is in the
    /// template's language) holds one sketch point; failing that, its point is selected by the feature's own name.
    /// </summary>
    bool SelectOrigin()
    {
        for (var f = _doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
        {
            if (f.GetTypeName2() != "OriginProfileFeature") continue;
            if ((f.GetSpecificFeature2() as ISketch)?.GetSketchPoints2() is object[] points && points.OfType<ISketchPoint>().FirstOrDefault() is ISketchPoint origin)
                return origin.Select4(true, null);
            return _doc.Extension.SelectByID2("Point1@" + f.Name, "EXTSKETCHPOINT", 0, 0, 0, true, 0, null, 0);
        }
        return false;
    }

    /// <summary>Where a dimension's text goes, in model space: 5 mm off the first entity's middle, in the sketch.</summary>
    double[] TextLocation(IReadOnlyList<object> entities)
    {
        Point2 at = entities[0] switch
        {
            ISketchLine l => Middle(Mm((ISketchPoint)l.GetStartPoint2()), Mm((ISketchPoint)l.GetEndPoint2())),
            ISketchArc a => Mm((ISketchPoint)a.GetCenterPoint2()),
            ISketchPoint p => Mm(p),
            _ => new Point2(0, 0),
        };
        var toModel = (MathTransform)Active.ModelToSketchTransform.Inverse();
        var t = (double[])toModel.ArrayData;
        double x = (at.X + 5) / M, y = (at.Y + 5) / M;
        return new[] { x * t[0] + y * t[3] + t[9], x * t[1] + y * t[4] + t[10], x * t[2] + y * t[5] + t[11] };
    }

    /// <summary>The non-construction segments a call added, found by the segment ids it left behind.</summary>
    IReadOnlyList<object> Drawn(Func<object> draw, string what)
    {
        var before = new HashSet<string>(AllSegments().Select(Id));
        if (draw() == null) throw Refused(what);
        var added = AllSegments().Where(s => !before.Contains(Id(s)) && !s.ConstructionGeometry).Cast<object>().ToList();
        if (added.Count == 0) throw Refused(what);
        return added;
    }

    IEnumerable<ISketchSegment> AllSegments() => Active.GetSketchSegments() is object[] all ? all.OfType<ISketchSegment>() : Enumerable.Empty<ISketchSegment>();

    static string Id(ISketchSegment s) => string.Join(",", (int[])s.GetID());

    static IReadOnlyList<object> Segments(object? made, string what) =>
        made is object[] all && all.Length > 0 ? all.OfType<ISketchSegment>().Cast<object>().ToList() : throw Refused(what);

    static Point2 Mm(ISketchPoint p) => new Point2(p.X * M, p.Y * M);

    static Point2 Middle(Point2 a, Point2 b) => new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    static SwAgentException Refused(string what) => new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not draw the {what}");

    static string ConstraintName(SketchRelationType type, bool points) => type switch
    {
        SketchRelationType.Horizontal => points ? "sgHORIZONTALPOINTS2D" : "sgHORIZONTAL2D",
        SketchRelationType.Vertical => points ? "sgVERTICALPOINTS2D" : "sgVERTICAL2D",
        SketchRelationType.Coincident => "sgCOINCIDENT",
        SketchRelationType.Tangent => "sgTANGENT",
        SketchRelationType.Parallel => "sgPARALLEL",
        SketchRelationType.Perpendicular => "sgPERPENDICULAR",
        SketchRelationType.Equal => "sgSAMELENGTH",
        SketchRelationType.Concentric => "sgCONCENTRIC",
        SketchRelationType.Midpoint => "sgATMIDDLE",
        SketchRelationType.Collinear => "sgCOLINEAR",
        SketchRelationType.Symmetric => "sgSYMMETRIC",
        _ => "sgFIXED",
    };
}

/// <summary>Reads a sketch back through SOLIDWORKS: segments, points, relations, dimensions, solve state and plane. UI thread only.</summary>
internal static class SolidWorksSketchReader
{
    const double M = 1000.0;

    public static SketchContents Read(IFeature feature)
    {
        var sketch = feature.GetSpecificFeature2() as ISketch
            ?? throw new SwAgentException(ErrorCode.InvalidArgument, $"{feature.Name} is not a sketch");
        var c = new SketchContents { Name = feature.Name, Feature = feature, Solve = Solve(sketch.GetConstrainedStatus()) };
        var t = (double[])((MathTransform)sketch.ModelToSketchTransform.Inverse()).ArrayData;
        c.XAxis = SolidWorksDocument.Unit(t[0], t[1], t[2]);
        c.YAxis = SolidWorksDocument.Unit(t[3], t[4], t[5]);
        c.Normal = SolidWorksDocument.Unit(t[6], t[7], t[8]);
        c.Origin = new Point3(t[9] * M, t[10] * M, t[11] * M);
        if (sketch.GetSketchSegments() is object[] segments)
        {
            foreach (var s in segments.OfType<ISketchSegment>()) c.Segments.Add(Segment(s));
        }
        if (sketch.GetSketchPoints2() is object[] points)
        {
            foreach (var p in points.OfType<ISketchPoint>()) c.Points.Add(new SketchPointRecord(p, new Point2(p.X * M, p.Y * M)));
        }
        if (sketch.RelationManager.GetRelations((int)swSketchRelationFilterType_e.swAll) is object[] relations)
        {
            foreach (var r in relations.OfType<ISketchRelation>())
            {
                var entities = r.GetEntities() is object[] e ? e.Where(x => x != null).ToList() : new List<object>();
                c.Relations.Add(new SketchRelationRecord(r.GetRelationType(), entities));
            }
        }
        c.Dimensions.AddRange(Dimensions(feature));
        return c;
    }

    public static IEnumerable<DimensionRecord> Dimensions(IFeature feature)
    {
        for (var d = feature.GetFirstDisplayDimension() as IDisplayDimension; d != null; d = feature.GetNextDisplayDimension(d) as IDisplayDimension)
        {
            if (!(d.GetDimension2(0) is IDimension dimension)) continue;
            bool angle = d.Type2 == (int)swDimensionType_e.swAngularDimension;
            double system = ((double[])dimension.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null))[0];
            yield return new DimensionRecord(d, SolidWorksCodes.DimensionName(dimension.FullName), angle ? system * 180 / Math.PI : system * M, angle,
                dimension.DrivenState != (int)swDimensionDrivenState_e.swDimensionDriven, feature.Name);
        }
    }

    static SketchSegmentRecord Segment(ISketchSegment s)
    {
        var kind = s.GetType() switch
        {
            (int)swSketchSegments_e.swSketchLINE => SketchSegmentKind.Line,
            (int)swSketchSegments_e.swSketchARC => s is ISketchArc arc && arc.IsCircle() == 1 ? SketchSegmentKind.Circle : SketchSegmentKind.Arc,
            (int)swSketchSegments_e.swSketchELLIPSE => SketchSegmentKind.Ellipse,
            (int)swSketchSegments_e.swSketchSPLINE => SketchSegmentKind.Spline,
            _ => SketchSegmentKind.Other,
        };
        var record = new SketchSegmentRecord(s, kind, s.GetName(), s.ConstructionGeometry) { LengthMm = s.GetLength() * M };
        switch (s)
        {
            case ISketchLine l:
                record.Start = At(l.GetStartPoint2());
                record.End = At(l.GetEndPoint2());
                break;
            case ISketchArc a:
                record.Center = At(a.GetCenterPoint2());
                record.RadiusMm = a.GetRadius() * M;
                if (kind == SketchSegmentKind.Arc)
                {
                    record.Start = At(a.GetStartPoint2());
                    record.End = At(a.GetEndPoint2());
                }
                break;
        }
        return record;
    }

    static Point2? At(object? point) => point is ISketchPoint p ? new Point2(p.X * M, p.Y * M) : null;

    static SolveState Solve(int status) => status switch
    {
        (int)swConstrainedStatus_e.swFullyConstrained => SolveState.FullyDefined,
        (int)swConstrainedStatus_e.swUnderConstrained => SolveState.UnderDefined,
        (int)swConstrainedStatus_e.swOverConstrained => SolveState.OverDefined,
        (int)swConstrainedStatus_e.swNoSolution => SolveState.NoSolution,
        (int)swConstrainedStatus_e.swInvalidSolution => SolveState.NoSolution,
        _ => SolveState.Unknown,
    };
}
