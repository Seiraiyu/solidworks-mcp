using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Phase 6.B's flanges and hems on a <see cref="FakeDocument"/>'s sheet: an edge flange is made from the profile drawn in
/// its sketch, and adds what the flange geometry says that profile holds (its height along the flange, its width along
/// the edge), with a bend whose cylindrical faces lie along the edge, and raises the part's exact box by its rise; a miter
/// flange adds its profile's length times its edges' length times the thickness, with a bend per profile corner and edge;
/// a hem adds the flange geometry's material and consumes its edges. Deleting any of them takes everything back.
/// </summary>
sealed partial class FakeDocument
{
    readonly Dictionary<FakeEntity, List<FakeEntity>> _adjacent = new Dictionary<FakeEntity, List<FakeEntity>>();

    /// <summary>
    /// An edge of the sheet's base face from <paramref name="start"/> to <paramref name="end"/>, with the thin face the
    /// sheet ends in there (its normal <paramref name="outward"/>, as wide as the thickness).
    /// </summary>
    public FakeEntity SheetEdge(Point3 start, Point3 end, Vector3 outward)
    {
        var face = SheetFaceList[0];
        double t = Sheet!.ThicknessMm, dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z, length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var n = face.Info.Normal!;
        var middle = new Point3((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2);
        var edge = Add(ReferenceKind.Edge, new EntityInfo { Feature = "Base-Flange1", Geometry = GeometryType.Line, LengthMm = length, Axis = new Vector3(dx / length, dy / length, dz / length), Origin = middle });
        var side = Add(ReferenceKind.Face, new EntityInfo { Feature = "Base-Flange1", Geometry = GeometryType.Plane, Normal = outward, AreaMm2 = length * t, Origin = new Point3(middle.X - n.X * t / 2, middle.Y - n.Y * t / 2, middle.Z - n.Z * t / 2) });
        _adjacent[edge] = new List<FakeEntity> { face, side };
        return edge;
    }

    public IReadOnlyList<object> AdjacentFaces(object edge) =>
        _adjacent.TryGetValue((FakeEntity)edge, out var faces) ? faces.Where(Entities.Contains).Cast<object>().ToList() : new List<object>();

    public List<(object Edge, double AngleDeg)> EdgeFlangeSketches { get; } = new List<(object, double)>();

    public List<(object Edge, object Sketch, EdgeFlangeSpec Spec)> EdgeFlanges { get; } = new List<(object, object, EdgeFlangeSpec)>();

    /// <summary>SOLIDWORKS makes no sketch for an edge flange.</summary>
    public bool EdgeSketchRefuses { get; set; }

    /// <summary>A flange's end rises this much more than its geometry says.</summary>
    public double FlangeRiseError { get; set; }

    /// <summary>The flange's bends read back this much off the angle asked.</summary>
    public double BendAngleError { get; set; }

    static Vector3 FlangeDirection(Vector3 normal, Vector3 outward, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180;
        return new Vector3(Math.Cos(a) * outward.X + Math.Sin(a) * normal.X, Math.Cos(a) * outward.Y + Math.Sin(a) * normal.Y, Math.Cos(a) * outward.Z + Math.Sin(a) * normal.Z);
    }

    /// <summary>A sketch on the plane through the edge along the flange's direction: x along the edge from its start, y along the flange.</summary>
    public object? EdgeFlangeSketch(object edge, double angleDeg)
    {
        EdgeFlangeSketches.Add((edge, angleDeg));
        if (EdgeSketchRefuses) return null;
        var e = (FakeEntity)edge;
        var faces = _adjacent[e];
        var (start, _) = FlangeGeometry.Ends(e.Info.Origin!, e.Info.Axis!, e.Info.LengthMm!.Value);
        var x = e.Info.Axis!;
        var y = FlangeDirection(faces[0].Info.Normal!, faces[1].Info.Normal!, angleDeg);
        var feature = AddFeature("Sketch" + (Sketches.Count + 1), "ProfileFeature", ReferenceKind.Sketch);
        var sketch = new FakeSketch(this, feature, edge) { Frame = (start, x, y, new Vector3(x.Y * y.Z - x.Z * y.Y, x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X)) };
        Sketches.Add(sketch);
        return feature;
    }

    public object? EdgeFlange(object edge, object sketch, EdgeFlangeSpec spec)
    {
        EdgeFlanges.Add((edge, sketch, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the edge a feature needs");
        if (FeatureRefuses || Sheet == null) return null;
        var points = ReadSketch(sketch).Segments.Where(s => !s.Construction && s.Start != null && s.End != null).SelectMany(s => new[] { s.Start!, s.End! }).ToList();
        if (points.Count == 0) return MakeFeature("Edge-Flange", "EdgeFlange", 0, 0);
        double height = points.Max(p => p.Y), from = points.Min(p => p.X), to = points.Max(p => p.X), width = to - from;
        double radius = spec.RadiusMm ?? Sheet.BendRadiusMm, t = Sheet.ThicknessMm, half = Math.Tan(spec.AngleDeg * Math.PI / 360);
        double outerSharp = spec.Position switch
        {
            BendPosition.MaterialOutside => t * half,
            BendPosition.BendOutside => (radius + t) * half,
            _ => 0,
        };
        double inner = height - (outerSharp - t * half) * Math.Cos(spec.AngleDeg * Math.PI / 180);
        var section = FlangeGeometry.EdgeFlange(spec.AngleDeg, radius, t, inner, FlangeLengthFrom.InnerVirtualSharp, spec.Position);
        if (section == null) return null;
        var flange = MakeFeature("Edge-Flange", "EdgeFlange", width * section.AddedPerWidthMm2, 3);
        string name = flange.Info.Name!;
        var e = (FakeEntity)edge;
        var face = _adjacent[e][0];
        var axis = e.Info.Axis!;
        var (start, _) = FlangeGeometry.Ends(e.Info.Origin!, axis, e.Info.LengthMm!.Value);
        Point3 At(double s) => new Point3(start.X + axis.X * s, start.Y + axis.Y * s, start.Z + axis.Z * s);
        var (a, b) = (At(from), At(to));
        var box = new Box3 { Min = new Point3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), Max = new Point3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)) };
        var bend = new FakeBend
        {
            Feature = AddFeature($"EdgeBend{FakeBends.Count(x => x.Owner.StartsWith("Edge-Flange", StringComparison.Ordinal)) + 1}", "OneBend", parent: name),
            Owner = name,
            AngleDeg = spec.AngleDeg + BendAngleError,
            RadiusMm = radius,
            Allowance = spec.Allowance ?? Sheet.Allowance,
            DefaultRadius = spec.RadiusMm == null,
            DefaultAllowance = spec.Allowance == null,
            WidthMm = width,
        };
        bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = name, Geometry = GeometryType.Cylinder, RadiusMm = radius, Axis = axis, Box = box }));
        bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = name, Geometry = GeometryType.Cylinder, RadiusMm = radius + t, Axis = axis, Box = box }));
        _facesOf[bend.Feature] = bend.Faces;
        FakeBends.Add(bend);
        var boxBefore = ExactBox;
        if (ExactBox != null && section.RiseMm != null) ExactBox = Reach(ExactBox, face.Info.Normal!, Along(face.Info.Normal!, face.Info.Origin!) + section.RiseMm.Value + FlangeRiseError);
        Undo(flange, () =>
        {
            RemoveFeature(bend.Feature);
            Entities.RemoveAll(bend.Faces.Contains);
            FakeBends.Remove(bend);
            ExactBox = boxBefore;
        });
        return flange;
    }

    static double Along(Vector3 n, Point3 p) => n.X * p.X + n.Y * p.Y + n.Z * p.Z;

    /// <summary>The box grown so that it reaches <paramref name="level"/> along the axis-aligned direction <paramref name="n"/>.</summary>
    static Box3 Reach(Box3 box, Vector3 n, double level)
    {
        double[] min = { box.Min.X, box.Min.Y, box.Min.Z }, max = { box.Max.X, box.Max.Y, box.Max.Z };
        double[] d = { n.X, n.Y, n.Z };
        int i = Array.FindIndex(d, c => Math.Abs(c) > 0.999);
        if (i < 0) return box;
        if (d[i] > 0) max[i] = Math.Max(max[i], level);
        else min[i] = Math.Min(min[i], -level);
        return new Box3 { Min = new Point3(min[0], min[1], min[2]), Max = new Point3(max[0], max[1], max[2]) };
    }

    public List<(object Sketch, IReadOnlyList<object> Edges, MiterFlangeSpec Spec)> MiterFlanges { get; } = new List<(object, IReadOnlyList<object>, MiterFlangeSpec)>();

    /// <summary>The miter flange is made along the first edge only (CADapter: two edges gave one flange).</summary>
    public bool MiterFirstEdgeOnly { get; set; }

    public object? MiterFlange(object sketch, IReadOnlyList<object> edges, MiterFlangeSpec spec)
    {
        MiterFlanges.Add((sketch, edges, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch a feature needs");
        if (FeatureRefuses || Sheet == null) return null;
        var contents = ReadSketch(sketch);
        var lines = contents.Segments.Where(s => !s.Construction && s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null).ToList();
        var chain = BendArithmetic.Chain(lines.Select(s => (s.Start!, s.End!)).ToList());
        if (chain == null) return null;
        double profile = lines.Sum(s => Math.Sqrt(Math.Pow(s.End!.X - s.Start!.X, 2) + Math.Pow(s.End!.Y - s.Start!.Y, 2)));
        var along = MiterFirstEdgeOnly ? edges.Take(1).ToList() : edges.ToList();
        double length = along.Cast<FakeEntity>().Sum(e => e.Info.LengthMm ?? 0);
        var flange = MakeFeature("Miter Flange", "SMMiteredFlange", length * Sheet.ThicknessMm * profile, 4);
        string name = flange.Info.Name!;
        var made = new List<FakeBend>();
        var corners = BendArithmetic.OpenProfile(chain, spec.RadiusMm ?? Sheet.BendRadiusMm, Sheet.ThicknessMm, 1).AnglesDeg;
        foreach (var e in along.Cast<FakeEntity>())
        {
            foreach (double angle in new[] { 90.0 }.Concat(corners))
            {
                var bend = new FakeBend
                {
                    Feature = AddFeature($"MiterBend{FakeBends.Count + 1}", "OneBend", parent: name),
                    Owner = name,
                    AngleDeg = angle,
                    RadiusMm = spec.RadiusMm ?? Sheet.BendRadiusMm,
                    Allowance = spec.Allowance ?? Sheet.Allowance,
                    DefaultRadius = spec.RadiusMm == null,
                    DefaultAllowance = spec.Allowance == null,
                    WidthMm = e.Info.LengthMm ?? 0,
                };
                FakeBends.Add(bend);
                made.Add(bend);
            }
        }
        var boxBefore = ExactBox;
        var only = FeatureFacts.OnlyLine(contents);
        if (ExactBox != null && only != null)
        {
            var s = lines[0].Start!;
            var start = new Point3(contents.Origin.X + s.X * contents.XAxis.X + s.Y * contents.YAxis.X, contents.Origin.Y + s.X * contents.XAxis.Y + s.Y * contents.YAxis.Y, contents.Origin.Z + s.X * contents.XAxis.Z + s.Y * contents.YAxis.Z);
            var d = only.Value.Direction;
            var tip = new Point3(start.X + d.X, start.Y + d.Y, start.Z + d.Z);
            ExactBox = Include(Include(ExactBox, start), tip);
        }
        Undo(flange, () =>
        {
            foreach (var b in made)
            {
                RemoveFeature(b.Feature);
                FakeBends.Remove(b);
            }
            ExactBox = boxBefore;
        });
        return flange;
    }

    static Box3 Include(Box3 box, Point3 p) => new Box3
    {
        Min = new Point3(Math.Min(box.Min.X, p.X), Math.Min(box.Min.Y, p.Y), Math.Min(box.Min.Z, p.Z)),
        Max = new Point3(Math.Max(box.Max.X, p.X), Math.Max(box.Max.Y, p.Y), Math.Max(box.Max.Z, p.Z)),
    };

    public List<(IReadOnlyList<object> Edges, HemSpec Spec)> Hems { get; } = new List<(IReadOnlyList<object>, HemSpec)>();

    /// <summary>A hem adds this much more than the flange geometry says.</summary>
    public double HemVolumeError { get; set; }

    public object? Hem(IReadOnlyList<object> edges, HemSpec spec)
    {
        Hems.Add((edges, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the edge a feature needs");
        if (FeatureRefuses || Sheet == null) return null;
        double width = edges.Cast<FakeEntity>().Sum(e => e.Info.LengthMm ?? 0);
        var position = spec.Inside ? BendPosition.MaterialInside : BendPosition.BendOutside;
        var material = FlangeGeometry.Hem(spec.Kind, Sheet.ThicknessMm, width, spec.LengthMm, spec.GapMm, spec.RadiusMm, spec.AngleDeg, position);
        var hem = MakeFeature("Hem", "Hem", material.ExpectedMm3 + HemVolumeError, 2 * edges.Count);
        if (!EdgesSurvive)
        {
            var consumed = edges.Cast<FakeEntity>().ToList();
            consumed.ForEach(e => e.State = PersistState.Deleted);
            Undo(hem, () => consumed.ForEach(e => e.State = PersistState.Ok));
        }
        return hem;
    }
}
