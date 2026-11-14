using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// The real document, through SOLIDWORKS' IModelDoc2. UI thread only. Converts SOLIDWORKS' metres to millimetres at
/// this boundary and nowhere else. Covered by the parts live suite, not by unit tests.
/// </summary>
internal sealed partial class SolidWorksDocument : ISwDocument
{
    const double Mm = 1000.0;
    const double Mm2 = 1e6;

    readonly ISldWorks _sw;

    public SolidWorksDocument(ISldWorks sw, IModelDoc2 doc)
    {
        _sw = sw;
        Doc = doc;
    }

    public IModelDoc2 Doc { get; }

    public bool ReadOnly => Doc.IsOpenedReadOnly();

    public string? EditingSketch()
    {
        if (!(Doc.GetActiveSketch2() is ISketch sketch)) return null;
        var active = new DrawingSketches.Sketch(sketch, (sketch as IFeature)?.Name);
        return Doc is IDrawingDoc drawing ? DrawingSketches.UserEditing(active, AtRest(drawing)) : active.Name ?? "a sketch";
    }

    /// <summary>A drawing's sketches at rest (DrawingSketches): the current sheet's own and the activated view's own.</summary>
    static IEnumerable<DrawingSketches.Sketch> AtRest(IDrawingDoc drawing)
    {
        foreach (var view in new[] { drawing.GetFirstView() as IView, drawing.ActiveDrawingView as IView })
        {
            if (view?.GetSketch() is ISketch own) yield return new DrawingSketches.Sketch(own, (own as IFeature)?.Name);
        }
    }

    public void Activate()
    {
        int errors = 0;
        _sw.ActivateDoc3(Doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
    }

    public void MarkDirty() => Doc.SetSaveFlag();

    public SaveOutcome Save()
    {
        int errors = 0, warnings = 0;
        bool saved = Doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
        return new SaveOutcome(saved, errors, warnings);
    }

    public SaveOutcome SaveAs(string path)
    {
        int errors = 0, warnings = 0;
        bool saved = Doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
        return new SaveOutcome(saved, errors, warnings);
    }

    public SaveOutcome Export(string path)
    {
        int errors = 0, warnings = 0;
        bool saved = Doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref errors, ref warnings);
        return new SaveOutcome(saved, errors, warnings);
    }

    public int PublishStep242(string path) => Doc.Extension.PublishSTEP242File(path);

    public int PublishStep242WithData(string path)
    {
        if (!(Doc.Extension.GetMBDSTEP242Data() is IMBDSTEP242Data data)) return (int)swStep242Error_e.swPublishStep242_UnknownError;
        data.PublishFileName = path;
        return Doc.Extension.PublishSTEP242File2(data);
    }

    public DocumentInfo Info() => SolidWorksApplication.Describe(Doc);

    public object ApiObject() => Doc;

    public byte[]? PersistId(object entity) => Doc.Extension.GetPersistReference3(entity) as byte[];

    public object? Resolve(byte[] persistId, out PersistState state)
    {
        int error;
        object? entity = Doc.Extension.GetObjectByPersistReference3(persistId, out error);
        state = error switch
        {
            (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok => PersistState.Ok,
            (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Suppressed => PersistState.Suppressed,
            (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Deleted => PersistState.Deleted,
            _ => PersistState.Invalid,
        };
        return entity;
    }

    public bool Rebuild(bool force) => force ? Doc.ForceRebuild3(false) : Doc.EditRebuild3();

    public IReadOnlyList<RebuildItem> WhatsWrong()
    {
        object features, codes, warnings;
        if (!Doc.Extension.GetWhatsWrong(out features, out codes, out warnings)) return new List<RebuildItem>();
        return WhatsWrongItems(features, codes, warnings, f => (f as IFeature)?.Name);
    }

    /// <summary>
    /// The What's Wrong arrays as items. COM may hand them back as object[] as well as int[] and bool[], so each element
    /// is converted rather than the arrays cast (as SolidWorksEventBridge.CountWhatsWrong does).
    /// </summary>
    internal static IReadOnlyList<RebuildItem> WhatsWrongItems(object? features, object? codes, object? warnings, Func<object?, string?> nameOf)
    {
        var items = new List<RebuildItem>();
        if (!(features is Array f) || !(codes is Array c) || !(warnings is Array w)) return items;
        for (int i = 0; i < f.Length && i < c.Length && i < w.Length; i++)
        {
            items.Add(new RebuildItem
            {
                Feature = nameOf(f.GetValue(i)) ?? "?",
                Code = SolidWorksCodes.FeatureError(Convert.ToInt32(c.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)),
                Warning = Convert.ToBoolean(w.GetValue(i), System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return items;
    }

    public IReadOnlyList<FeatureRecord> Features()
    {
        var walked = new List<(FeatureRecord, IReadOnlyList<FeatureRecord>)>();
        foreach (var top in TopFeatures())
        {
            var subs = new List<FeatureRecord>();
            for (var sub = top.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature) subs.Add(Record(sub, top.Name));
            walked.Add((Record(top, null), subs));
        }
        return FeatureTree.Arrange(walked);
    }

    static FeatureRecord Record(IFeature f, string? parent)
    {
        bool warning;
        int code = f.GetErrorCode2(out warning);
        return new FeatureRecord(f, f.Name, f.GetTypeName2(), parent, f.IsSuppressed(), code, warning);
    }

    object[] SolidBodies() => Doc is IPartDoc part && part.GetBodies2((int)swBodyType_e.swSolidBody, true) is object[] bodies ? bodies : new object[0];

    public int SolidBodyCount() => Doc is IAssemblyDoc ? ComponentBodies().Count() : SolidBodies().Length;

    /// <summary>
    /// An assembly's solid bodies: those of every resolved part component, nested ones included, each with its component
    /// (SOLIDWORKS gives a component body's geometry in the component's own coordinates).
    /// </summary>
    IEnumerable<(IBody2 Body, IComponent2 Component)> ComponentBodies()
    {
        if (!(Doc.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) is IComponent2 root)) yield break;
        var pending = new Stack<IComponent2>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!(parent.GetChildren() is object[] children)) continue;
            foreach (var child in children.OfType<IComponent2>())
            {
                if (child.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentResolved
                    && child.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentFullyResolved) continue;
                if ((child.GetPathName() ?? "").EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                    continue;
                }
                object bodyInfo;
                if (child.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) is object[] bodies)
                {
                    foreach (var body in bodies.OfType<IBody2>()) yield return (body, child);
                }
            }
        }
    }

    public ModelMassResult? Mass()
    {
        if (Doc is IAssemblyDoc)
        {
            // An assembly's: every component that is not suppressed, hidden ones included, as Mass(component) counts each.
            if (!(Doc.Extension.CreateMassProperty2() is IMassProperty2 whole)) return null;
            whole.UseSystemUnits = true;
            whole.IncludeHiddenBodiesOrComponents = true;
            return MassOf(whole);
        }
        if (!(Doc.Extension.CreateMassProperty() is IMassProperty mp)) return null;
        mp.UseSystemUnits = true;
        var com = (double[])mp.CenterOfMass;
        var moments = (double[])mp.PrincipleMomentsOfInertia;
        return new ModelMassResult
        {
            VolumeMm3 = mp.Volume * 1e9,
            AreaMm2 = mp.SurfaceArea * Mm2,
            MassG = mp.Mass * 1000,
            DensityKgM3 = mp.Density,
            CenterOfMass = new Point3(com[0] * Mm, com[1] * Mm, com[2] * Mm),
            PrincipalMomentsGMm2 = moments.Take(3).Select(m => m * 1e9).ToList(),
        };
    }

    /// <summary>What an IMassProperty2 read in SI units says, in mm and g.</summary>
    internal static ModelMassResult MassOf(IMassProperty2 mp)
    {
        var com = (double[])mp.CenterOfMass;
        var moments = (double[])mp.PrincipalMomentsOfInertia;
        return new ModelMassResult
        {
            VolumeMm3 = mp.Volume * 1e9,
            AreaMm2 = mp.SurfaceArea * Mm2,
            MassG = mp.Mass * 1000,
            DensityKgM3 = mp.Density,
            CenterOfMass = new Point3(com[0] * Mm, com[1] * Mm, com[2] * Mm),
            PrincipalMomentsGMm2 = moments.Take(3).Select(m => m * 1e9).ToList(),
        };
    }

    public string? Material()
    {
        if (!(Doc is IPartDoc part)) return null;
        string database;
        string name = part.GetMaterialPropertyName2("", out database);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public Box3? BoundingBox(bool precise)
    {
        if (Doc is IAssemblyDoc assembly) return AssemblyBox(assembly, precise);
        if (!precise) return Doc is IPartDoc part && part.GetPartBox(true) is double[] b ? Box(b) : null;
        double[]? box = null;
        foreach (var o in SolidBodies())
        {
            if (!(o is IBody2 body)) continue;
            var lo = new double[3];
            var hi = new double[3];
            for (int axis = 0; axis < 3; axis++)
            {
                lo[axis] = Extreme(body, axis, -1);
                hi[axis] = Extreme(body, axis, 1);
            }
            box = box == null
                ? new[] { lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] }
                : new[] { Math.Min(box[0], lo[0]), Math.Min(box[1], lo[1]), Math.Min(box[2], lo[2]), Math.Max(box[3], hi[0]), Math.Max(box[4], hi[1]), Math.Max(box[5], hi[2]) };
        }
        return box == null ? null : Box(box);
    }

    /// <summary>
    /// An assembly's box: SOLIDWORKS' quick one of its solid geometry (IAssemblyDoc.GetBox, no planes or sketches), or
    /// the exact one from the extreme points of a copy of each component body moved by its component's placement (a
    /// component body's own geometry is in the component's coordinates). Null with no solid body.
    /// </summary>
    Box3? AssemblyBox(IAssemblyDoc assembly, bool precise)
    {
        if (!ComponentBodies().Any()) return null;
        if (!precise) return assembly.GetBox(0) is double[] quick && quick.Length >= 6 ? Box(quick) : null;
        double[]? box = null;
        foreach (var (body, component) in ComponentBodies())
        {
            if (!(body.Copy() is IBody2 copy)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS could not copy a body of {component.Name2} to measure it");
            if (!copy.ApplyTransform(component.Transform2)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS could not move a copy of a body of {component.Name2} into the assembly");
            var lo = new double[3];
            var hi = new double[3];
            for (int axis = 0; axis < 3; axis++)
            {
                lo[axis] = Extreme(copy, axis, -1);
                hi[axis] = Extreme(copy, axis, 1);
            }
            box = box == null
                ? new[] { lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] }
                : new[] { Math.Min(box[0], lo[0]), Math.Min(box[1], lo[1]), Math.Min(box[2], lo[2]), Math.Max(box[3], hi[0]), Math.Max(box[4], hi[1]), Math.Max(box[5], hi[2]) };
        }
        return box == null ? null : Box(box);
    }

    /// <summary>The body's farthest coordinate along <paramref name="axis"/> in direction <paramref name="sign"/>, in metres.</summary>
    static double Extreme(IBody2 body, int axis, int sign)
    {
        double x, y, z;
        if (!body.GetExtremePoint(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0, out x, out y, out z))
            throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS gave no extreme point for a body");
        return axis == 0 ? x : axis == 1 ? y : z;
    }

    public IReadOnlyList<object> Topology(TopologyKind kind)
    {
        var list = new List<object>();
        foreach (var o in SolidBodies())
        {
            if (!(o is IBody2 body)) continue;
            object? items = kind switch
            {
                TopologyKind.Edge => body.GetEdges(),
                TopologyKind.Vertex => body.GetVertices(),
                _ => body.GetFaces(),
            };
            if (items is object[] array) list.AddRange(array);
        }
        return list;
    }

    public double DistanceMm(object entity, Point3 point) => Distance(entity, point);

    /// <summary>The shortest distance from <paramref name="point"/> (mm, in the space SOLIDWORKS gives the entity's geometry in) to a face, edge or vertex, in mm.</summary>
    internal static double Distance(object entity, Point3 point)
    {
        double x = point.X / Mm, y = point.Y / Mm, z = point.Z / Mm;
        double[] closest = entity switch
        {
            IFace2 face => (double[])face.GetClosestPointOn(x, y, z),
            IEdge edge => (double[])edge.GetClosestPointOn(x, y, z),
            IVertex vertex => (double[])vertex.GetPoint(),
            _ => throw new SwAgentException(ErrorCode.ApiError, $"cannot measure to a {entity.GetType().Name}"),
        };
        double dx = closest[0] - x, dy = closest[1] - y, dz = closest[2] - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) * Mm;
    }

    public int SelectionCount() => Doc.SelectionManager is ISelectionMgr manager ? manager.GetSelectedObjectCount2(-1) : 0;

    public IReadOnlyList<SelectedObject> Selection(int max)
    {
        var list = new List<SelectedObject>();
        if (!(Doc.SelectionManager is ISelectionMgr manager)) return list;
        int count = Math.Min(manager.GetSelectedObjectCount2(-1), max);
        for (int i = 1; i <= count; i++) list.Add(new SelectedObject(manager.GetSelectedObjectType3(i, -1), manager.GetSelectedObject6(i, -1)));
        return list;
    }

    public void ClearSelection() => Doc.ClearSelection2(true);

    public bool Select(object entity, bool append) => entity switch
    {
        IComponent2 component => component.Select4(append, null, false),
        IFeature feature => feature.Select2(append, 0),
        IEntity e => e.Select4(append, null),
        _ => false,
    };

    public object? ComponentOf(object entity)
    {
        if (!(Doc is IAssemblyDoc) || entity is IComponent2) return null;
        try
        {
            return (entity as IEntity)?.GetComponent() as IComponent2;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public ReferenceKind? KindOf(object entity) => entity switch
    {
        IComponent2 _ => ReferenceKind.Component,
        IFeature f => FeatureKind(f.GetTypeName2()),
        IFace2 _ => ReferenceKind.Face,
        IEdge _ => ReferenceKind.Edge,
        IVertex _ => ReferenceKind.Vertex,
        ISketchSegment _ => ReferenceKind.SketchEntity,
        ISketchPoint _ => ReferenceKind.SketchPoint,
        IDisplayDimension _ => ReferenceKind.Dimension,
        _ => null,
    };

    /// <summary>The kind a feature of this type (IFeature.GetTypeName2) is named as.</summary>
    internal static ReferenceKind FeatureKind(string typeName) => typeName switch
    {
        "RefPlane" => ReferenceKind.Plane,
        "RefAxis" => ReferenceKind.Axis,
        "RefPoint" => ReferenceKind.Point,
        "OriginProfileFeature" => ReferenceKind.Point,
        "ProfileFeature" => ReferenceKind.Sketch,
        "3DProfileFeature" => ReferenceKind.Sketch,
        // Mates are features of the assembly's MateGroup: MateCoincident, MateDistanceDim, MatePlanarAngleDim, MateLock, ...
        _ => typeName.StartsWith("Mate", StringComparison.Ordinal) && typeName != "MateGroup" && !typeName.StartsWith("MateReference", StringComparison.Ordinal)
            ? ReferenceKind.Mate
            : ReferenceKind.Feature,
    };

    public EntityInfo Describe(object entity) => entity switch
    {
        IComponent2 c => new EntityInfo { Kind = ReferenceKind.Component, Name = c.Name2, Geometry = GeometryType.Other },
        IFeature f => DescribeFeature(f),
        IFace2 face => DescribeFace(face),
        IEdge edge => DescribeEdge(edge),
        IVertex vertex => DescribeVertex(vertex),
        ISketchSegment segment => DescribeSketchSegment(segment),
        ISketchPoint point => DescribeSketchPoint(point),
        IDisplayDimension dimension => DescribeDimension(dimension),
        _ => throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS returned a {entity.GetType().Name}, which the add-in cannot describe"),
    };

    public object? FeatureByName(string name)
    {
        foreach (var f in AllFeatures())
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal)) return f;
        }
        return null;
    }

    public object? StandardPlane(int index)
    {
        int seen = 0;
        foreach (var f in TopFeatures())
        {
            if (f.GetTypeName2() != "RefPlane") continue;
            if (seen++ == index) return f;
        }
        return null;
    }

    public object? Origin()
    {
        foreach (var f in TopFeatures())
        {
            if (f.GetTypeName2() == "OriginProfileFeature") return f;
        }
        return null;
    }

    /// <summary>The top-level features in tree order.</summary>
    internal IEnumerable<IFeature> TopFeatures()
    {
        for (var f = Doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature) yield return f;
    }

    /// <summary>Every feature in tree order, each followed by its sub-features (sketches under the feature that uses them).</summary>
    internal IEnumerable<IFeature> AllFeatures()
    {
        foreach (var top in TopFeatures())
        {
            yield return top;
            for (var sub = top.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature) yield return sub;
        }
    }

    static EntityInfo DescribeFeature(IFeature f)
    {
        string type = f.GetTypeName2();
        var info = new EntityInfo { Kind = FeatureKind(type), Name = f.Name, Geometry = GeometryType.Other };
        switch (f.GetSpecificFeature2())
        {
            case IRefPlane plane when type == "RefPlane":
            {
                var t = (double[])plane.Transform.ArrayData;
                info.Geometry = GeometryType.Plane;
                info.Normal = Unit(t[6], t[7], t[8]);
                info.Origin = new Point3(t[9] * Mm, t[10] * Mm, t[11] * Mm);
                break;
            }
            case IRefAxis axis:
            {
                var p = (double[])axis.GetRefAxisParams();
                info.Geometry = GeometryType.Line;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.Axis = Unit(p[3] - p[0], p[4] - p[1], p[5] - p[2]);
                break;
            }
            case IRefPoint point:
            {
                var p = (double[])point.GetRefPoint().ArrayData;
                info.Geometry = GeometryType.Point;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                break;
            }
        }
        if (type == "OriginProfileFeature")
        {
            info.Geometry = GeometryType.Point;
            info.Origin = new Point3(0, 0, 0);
        }
        return info;
    }

    public ISketchEditor NewSketch(object planeOrFace) => SolidWorksSketchEditor.Open(Doc, planeOrFace);

    public ISketchEditor EditSketch(object sketch) => SolidWorksSketchEditor.Edit(Doc, (IFeature)sketch);

    public SketchContents ReadSketch(object sketch) => SolidWorksSketchReader.Read((IFeature)sketch);

    public IReadOnlyList<DimensionRecord> Dimensions(object? feature)
    {
        var features = feature != null ? new[] { (IFeature)feature } : Features().Select(f => (IFeature)f.Feature);
        return features.SelectMany(SolidWorksSketchReader.Dimensions).ToList();
    }

    public void SetDimension(object dimension, double value)
    {
        var display = (IDisplayDimension)dimension;
        var d = (IDimension)display.GetDimension2(0);
        bool angle = display.Type2 == (int)swDimensionType_e.swAngularDimension;
        int status = d.SetSystemValue3(angle ? value * Math.PI / 180 : value / Mm, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
        if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not set {d.FullName} ({Enum.GetName(typeof(swSetValueReturnStatus_e), status) ?? status.ToString()})");
    }

    public void DeleteFeature(object feature)
    {
        Doc.ClearSelection2(true);
        if (!((IFeature)feature).Select2(false, 0)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select {((IFeature)feature).Name} to delete it");
        bool deleted = Doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
        Doc.ClearSelection2(true);
        if (!deleted) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not delete {((IFeature)feature).Name}");
    }

    static string? SketchName(object? sketch) => (sketch as IFeature)?.Name;

    static EntityInfo DescribeSketchSegment(ISketchSegment s)
    {
        var info = new EntityInfo
        {
            Kind = ReferenceKind.SketchEntity,
            Name = s.GetName(),
            Feature = SketchName(s.GetSketch()),
            LengthMm = s.GetLength() * Mm,
            Geometry = GeometryType.Other,
        };
        switch (s.GetType())
        {
            case (int)swSketchSegments_e.swSketchLINE:
                info.Geometry = GeometryType.Line;
                break;
            case (int)swSketchSegments_e.swSketchARC:
                info.Geometry = GeometryType.Circle;
                if (s is ISketchArc arc) info.RadiusMm = arc.GetRadius() * Mm;
                break;
            case (int)swSketchSegments_e.swSketchELLIPSE:
                info.Geometry = GeometryType.Ellipse;
                break;
            case (int)swSketchSegments_e.swSketchSPLINE:
                info.Geometry = GeometryType.Spline;
                break;
        }
        return info;
    }

    static EntityInfo DescribeSketchPoint(ISketchPoint p)
    {
        var info = new EntityInfo { Kind = ReferenceKind.SketchPoint, Feature = SketchName(p.GetSketch()), Geometry = GeometryType.Point };
        if (p.GetSketch() is ISketch sketch)
        {
            var t = (double[])((MathTransform)sketch.ModelToSketchTransform.Inverse()).ArrayData;
            info.Origin = new Point3((p.X * t[0] + p.Y * t[3] + t[9]) * Mm, (p.X * t[1] + p.Y * t[4] + t[10]) * Mm, (p.X * t[2] + p.Y * t[5] + t[11]) * Mm);
        }
        return info;
    }

    static EntityInfo DescribeDimension(IDisplayDimension d) => new EntityInfo
    {
        Kind = ReferenceKind.Dimension,
        Name = d.GetDimension2(0) is IDimension dimension ? SolidWorksCodes.DimensionName(dimension.FullName) : null,
        Geometry = GeometryType.Other,
    };

    static EntityInfo DescribeFace(IFace2 face)
    {
        var info = new EntityInfo
        {
            Kind = ReferenceKind.Face,
            Feature = (face.GetFeature() as IFeature)?.Name,
            AreaMm2 = face.GetArea() * Mm2,
            Box = Box((double[])face.GetBox()),
            Geometry = GeometryType.Other,
        };
        var surface = (ISurface)face.GetSurface();
        switch (surface.Identity())
        {
            case (int)swSurfaceTypes_e.PLANE_TYPE:
            {
                var n = (double[])face.Normal;
                info.Geometry = GeometryType.Plane;
                info.Normal = Unit(n[0], n[1], n[2]);
                info.Origin = Centre(info.Box!);
                break;
            }
            case (int)swSurfaceTypes_e.CYLINDER_TYPE:
            {
                var p = (double[])surface.CylinderParams;
                info.Geometry = GeometryType.Cylinder;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.Axis = Unit(p[3], p[4], p[5]);
                info.RadiusMm = p[6] * Mm;
                break;
            }
            case (int)swSurfaceTypes_e.CONE_TYPE:
            {
                var p = (double[])surface.ConeParams;
                info.Geometry = GeometryType.Cone;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.Axis = Unit(p[3], p[4], p[5]);
                info.RadiusMm = p[6] * Mm;
                break;
            }
            case (int)swSurfaceTypes_e.SPHERE_TYPE:
            {
                var p = (double[])surface.SphereParams;
                info.Geometry = GeometryType.Sphere;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.RadiusMm = p[3] * Mm;
                break;
            }
            case (int)swSurfaceTypes_e.TORUS_TYPE:
            {
                var p = (double[])surface.TorusParams;
                info.Geometry = GeometryType.Torus;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.Axis = Unit(p[3], p[4], p[5]);
                info.RadiusMm = p[6] * Mm;
                break;
            }
            case (int)swSurfaceTypes_e.BSURF_TYPE:
                info.Geometry = GeometryType.Spline;
                break;
        }
        return info;
    }

    static EntityInfo DescribeEdge(IEdge edge)
    {
        var curve = (ICurve)edge.GetCurve();
        var range = edge.GetCurveParams3();
        var features = FeaturesOfEdge(edge);
        var info = new EntityInfo
        {
            Kind = ReferenceKind.Edge,
            Feature = features.FirstOrDefault(),
            Features = features,
            LengthMm = curve.GetLength3(range.UMinValue, range.UMaxValue) * Mm,
            Geometry = GeometryType.Other,
        };
        switch (curve.Identity())
        {
            case (int)swCurveTypes_e.LINE_TYPE:
            {
                var p = (double[])curve.LineParams;
                info.Geometry = GeometryType.Line;
                info.Axis = Unit(p[3], p[4], p[5]);
                info.Origin = Midpoint(edge);
                break;
            }
            case (int)swCurveTypes_e.CIRCLE_TYPE:
            {
                var p = (double[])curve.CircleParams;
                info.Geometry = GeometryType.Circle;
                info.Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm);
                info.Axis = Unit(p[3], p[4], p[5]);
                info.RadiusMm = p[6] * Mm;
                break;
            }
            case (int)swCurveTypes_e.ELLIPSE_TYPE:
                info.Geometry = GeometryType.Ellipse;
                break;
            case (int)swCurveTypes_e.BCURVE_TYPE:
            case (int)swCurveTypes_e.SPCURVE_TYPE:
                info.Geometry = GeometryType.Spline;
                break;
        }
        return info;
    }

    static EntityInfo DescribeVertex(IVertex vertex)
    {
        var p = (double[])vertex.GetPoint();
        var features = new List<string>();
        if (vertex.GetEdges() is object[] edges)
        {
            foreach (var edge in edges.OfType<IEdge>())
            {
                foreach (var name in FeaturesOfEdge(edge))
                {
                    if (!features.Contains(name)) features.Add(name);
                }
            }
        }
        return new EntityInfo
        {
            Kind = ReferenceKind.Vertex,
            Feature = features.FirstOrDefault(),
            Features = features,
            Geometry = GeometryType.Point,
            Origin = new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm),
        };
    }

    /// <summary>The features that made an edge: the feature of every face it bounds, each once, in SOLIDWORKS' order.</summary>
    static List<string> FeaturesOfEdge(IEdge edge)
    {
        var names = new List<string>();
        if (!(edge.GetTwoAdjacentFaces2() is object[] faces)) return names;
        foreach (var face in faces.OfType<IFace2>())
        {
            if (face.GetFeature() is IFeature f && !names.Contains(f.Name)) names.Add(f.Name);
        }
        return names;
    }

    static Point3? Midpoint(IEdge edge)
    {
        if (!(edge.GetStartVertex() is IVertex start) || !(edge.GetEndVertex() is IVertex end)) return null;
        var a = (double[])start.GetPoint();
        var b = (double[])end.GetPoint();
        return new Point3((a[0] + b[0]) / 2 * Mm, (a[1] + b[1]) / 2 * Mm, (a[2] + b[2]) / 2 * Mm);
    }

    internal static Box3 Box(double[] b) => new Box3
    {
        Min = new Point3(Math.Min(b[0], b[3]) * Mm, Math.Min(b[1], b[4]) * Mm, Math.Min(b[2], b[5]) * Mm),
        Max = new Point3(Math.Max(b[0], b[3]) * Mm, Math.Max(b[1], b[4]) * Mm, Math.Max(b[2], b[5]) * Mm),
    };

    static Point3 Centre(Box3 box) => new Point3((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);

    internal static Vector3 Unit(double x, double y, double z)
    {
        double length = Math.Sqrt(x * x + y * y + z * z);
        return length == 0 ? new Vector3(0, 0, 0) : new Vector3(Clean(x / length), Clean(y / length), Clean(z / length));
    }

    /// <summary>Rounds away floating-point dust (1e-17 becomes 0) so directions read cleanly.</summary>
    static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : v;
}
