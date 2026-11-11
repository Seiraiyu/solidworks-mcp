using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Phase 6.C's bends on a <see cref="FakeDocument"/>'s sheet. A sketched bend makes a bend per line, as wide as the line,
/// and changes the volume by the bend arithmetic's folding; an Unfold or Fold feature takes its bends' cylindrical faces
/// out of the part or puts them back, changing the volume by the bend arithmetic's developing or folding; flattening
/// (unsuppressing Flat-Pattern1) does the same for every bend still whole; convert makes Sheet-Metal1, Convert-Solid1 and
/// Flat-Pattern1, a sheet of two faces from the fixed face and a 90° bend per edge. Deleting any of them takes it back.
/// </summary>
sealed partial class FakeDocument
{
    public List<(object Sketch, Point3 FixedPoint, SketchedBendSpec Spec)> SketchedBends { get; } = new List<(object, Point3, SketchedBendSpec)>();

    /// <summary>A sketched bend changes the volume this much more than the bend arithmetic says.</summary>
    public double BendVolumeError { get; set; }

    /// <summary>A sketched bend, unfold or fold moves the sheet's base face this far off where it was.</summary>
    public double FixedFaceMoves { get; set; }

    FakeBend MakeBend(string name, string owner, double angle, double radius, BendAllowance allowance, bool defaultRadius, bool defaultAllowance, Point3 from, Point3 to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z, width = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var axis = new Vector3(dx / width, dy / width, dz / width);
        var box = new Box3 { Min = new Point3(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Min(from.Z, to.Z)), Max = new Point3(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y), Math.Max(from.Z, to.Z)) };
        var bend = new FakeBend
        {
            Feature = AddFeature(name, "OneBend", parent: owner),
            Owner = owner,
            AngleDeg = angle + BendAngleError,
            RadiusMm = radius,
            Allowance = allowance,
            DefaultRadius = defaultRadius,
            DefaultAllowance = defaultAllowance,
            WidthMm = width,
        };
        bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = owner, Geometry = GeometryType.Cylinder, RadiusMm = radius, Axis = axis, Box = box }));
        bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = owner, Geometry = GeometryType.Cylinder, RadiusMm = radius + Sheet!.ThicknessMm, Axis = axis, Box = box }));
        _facesOf[bend.Feature] = bend.Faces;
        FakeBends.Add(bend);
        return bend;
    }

    void Unmake(IEnumerable<FakeBend> bends)
    {
        foreach (var b in bends.ToList())
        {
            RemoveFeature(b.Feature);
            Entities.RemoveAll(b.Faces.Contains);
            FakeBends.Remove(b);
        }
    }

    /// <summary>Moves the base face as <see cref="FixedFaceMoves"/> says, and puts it back when the feature is deleted.</summary>
    void MoveFixed(FakeEntity feature)
    {
        if (FixedFaceMoves == 0 || SheetFaceList.Count == 0) return;
        var face = SheetFaceList[0];
        double? before = face.Info.DistanceMm;
        face.Info.DistanceMm = FixedFaceMoves;
        Undo(feature, () => face.Info.DistanceMm = before);
    }

    static Point3 Model(SketchContents c, Point2 p) =>
        new Point3(c.Origin.X + p.X * c.XAxis.X + p.Y * c.YAxis.X, c.Origin.Y + p.X * c.XAxis.Y + p.Y * c.YAxis.Y, c.Origin.Z + p.X * c.XAxis.Z + p.Y * c.YAxis.Z);

    public object? SketchedBend(object sketch, Point3 fixedPoint, SketchedBendSpec spec)
    {
        SketchedBends.Add((sketch, fixedPoint, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the fixed face");
        if (FeatureRefuses || Sheet == null) return null;
        var contents = ReadSketch(sketch);
        var lines = contents.Segments.Where(s => !s.Construction && s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null).ToList();
        double radius = spec.RadiusMm ?? Sheet.BendRadiusMm;
        var allowance = spec.Allowance ?? Sheet.Allowance;
        double t = Sheet.ThicknessMm;
        double volume = -lines.Sum(l => BendArithmetic.FlattenChangeMm3(Math.Sqrt(Math.Pow(l.End!.X - l.Start!.X, 2) + Math.Pow(l.End.Y - l.Start.Y, 2)), spec.AngleDeg, radius, t, allowance));
        var feature = MakeFeature("Sketched Bend", "SM3dBend", volume + BendVolumeError, 2 * lines.Count);
        if (volume + BendVolumeError == 0) _volumeOf[feature] = 0;
        string name = feature.Info.Name!;
        var made = lines.Select(l => MakeBend($"SketchBend{FakeBends.Count + 1}", name, spec.AngleDeg, radius, allowance, spec.RadiusMm == null, spec.Allowance == null, Model(contents, l.Start!), Model(contents, l.End!))).ToList();
        MoveFixed(feature);
        Undo(feature, () => Unmake(made));
        return feature;
    }

    readonly List<FoldRecord> _folds = new List<FoldRecord>();

    public List<(object FixedFace, IReadOnlyList<object> Bends, bool Unfold)> FoldCalls { get; } = new List<(object, IReadOnlyList<object>, bool)>();

    /// <summary>SOLIDWORKS makes the Unfold or Fold feature and develops nothing (CADapter: with the wrong selection).</summary>
    public bool FoldsDoNothing { get; set; }

    /// <summary>Folds() gives no fixed face (SOLIDWORKS not handing it back).</summary>
    public bool FoldsHideFixedFace { get; set; }

    public IReadOnlyList<FoldRecord> Folds() =>
        _folds.Where(f => Tree.Any(t => ReferenceEquals(t.Feature, f.Feature))).Select(f => FoldsHideFixedFace ? new FoldRecord(f.Feature, f.Name, f.Unfold, null, f.Bends) : f).ToList();

    public void Unfold(object fixedFace, IReadOnlyList<object> bends) => Folding(fixedFace, bends, unfold: true);

    public void Fold(object fixedFace, IReadOnlyList<object> bends) => Folding(fixedFace, bends, unfold: false);

    void Folding(object fixedFace, IReadOnlyList<object> bends, bool unfold)
    {
        FoldCalls.Add((fixedFace, bends, unfold));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the fixed face");
        if (FeatureRefuses || Sheet == null) return;
        var named = bends.Cast<FakeEntity>().Select(e => FakeBends.Single(b => ReferenceEquals(b.Feature, e))).ToList();
        double t = Sheet.ThicknessMm;
        double change = FoldsDoNothing ? 0 : named.Sum(b => BendArithmetic.FlattenChangeMm3(b.WidthMm, b.AngleDeg, b.RadiusMm, t, b.Allowance)) * (unfold ? 1 : -1);
        var feature = MakeFeature(unfold ? "UnFold" : "Fold", unfold ? SheetMetalParts.UnfoldType : SheetMetalParts.FoldType, change, 2);
        if (change == 0) _volumeOf[feature] = 0;
        var record = new FoldRecord(feature, feature.Info.Name!, unfold, fixedFace, named.Select(b => b.Feature.Info.Name!).ToList());
        _folds.Add(record);
        if (!FoldsDoNothing)
        {
            var faces = named.SelectMany(b => b.Faces).ToList();
            if (unfold) Entities.RemoveAll(faces.Contains);
            else Entities.AddRange(faces.Where(f => !Entities.Contains(f)));
            Undo(feature, () =>
            {
                if (unfold) Entities.AddRange(faces.Where(f => !Entities.Contains(f)));
                else Entities.RemoveAll(faces.Contains);
            });
        }
        MoveFixed(feature);
        Undo(feature, () => _folds.Remove(record));
    }

    /// <summary>The exact box when flattened; null leaves the box as it is.</summary>
    public Box3? FlatBox { get; set; }

    /// <summary>Flattening changes the volume this much more than the bend arithmetic says.</summary>
    public double FlattenVolumeError { get; set; }

    /// <summary>Flattening leaves the bends' cylindrical faces in the part.</summary>
    public bool FlattenKeepsBendFaces { get; set; }

    readonly List<FakeEntity> _flatHidden = new List<FakeEntity>();
    double _flatChange;
    Box3? _foldedBox;

    /// <summary>Flat-Pattern1 unsuppressed develops every bend still whole and hides every bend's faces; suppressed again, folds them back.</summary>
    partial void Suppressed(FakeEntity feature, bool suppressed)
    {
        if (Sheet == null || Tree.First(t => ReferenceEquals(t.Feature, feature)).Type != SheetMetalParts.FlatPatternType) return;
        if (!suppressed)
        {
            double t = Sheet.ThicknessMm;
            _flatChange = FakeBends.Where(b => b.Faces.Count(Entities.Contains) == 2).Sum(b => BendArithmetic.FlattenChangeMm3(b.WidthMm, b.AngleDeg, b.RadiusMm, t, b.Allowance)) + FlattenVolumeError;
            MassProperties!.VolumeMm3 += _flatChange;
            _flatHidden.Clear();
            if (!FlattenKeepsBendFaces) _flatHidden.AddRange(FakeBends.SelectMany(b => b.Faces).Where(Entities.Contains));
            Entities.RemoveAll(_flatHidden.Contains);
            _foldedBox = ExactBox;
            if (FlatBox != null) ExactBox = FlatBox;
        }
        else
        {
            MassProperties!.VolumeMm3 -= _flatChange;
            _flatChange = 0;
            Entities.AddRange(_flatHidden.Where(f => !Entities.Contains(f)));
            _flatHidden.Clear();
            if (_foldedBox != null) ExactBox = _foldedBox;
        }
    }

    public List<(object FixedFace, IReadOnlyList<object> Edges, ConvertSpec Spec)> Converts { get; } = new List<(object, IReadOnlyList<object>, ConvertSpec)>();

    /// <summary>The part's default allowance after a convert (SOLIDWORKS' own default).</summary>
    public BendAllowance ConvertAllowance { get; set; } = new BendAllowance { KFactor = 0.5 };

    /// <summary>Convert makes one bend fewer than its edges (CADapter: a second edge selected by coordinate was refused).</summary>
    public bool ConvertDropsAnEdge { get; set; }

    /// <summary>Convert grows the exact box this much along x.</summary>
    public double ConvertBoxGrowth { get; set; }

    public void ConvertToSheet(object fixedFace, IReadOnlyList<object> bendEdges, ConvertSpec spec)
    {
        Converts.Add((fixedFace, bendEdges, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the fixed face");
        if (FeatureRefuses) return;
        AddFeature("Sheet-Metal1", SheetMetalParts.SheetMetalType);
        var convert = MakeFeature("Convert-Solid", "SolidToSheetMetal", 0, 2);
        _volumeOf[convert] = 0;
        AddFeature("Flat-Pattern1", SheetMetalParts.FlatPatternType, suppressed: true);
        Sheet = new SheetDefaults { ThicknessMm = spec.ThicknessMm, BendRadiusMm = spec.BendRadiusMm, Allowance = ConvertAllowance, Relief = ReliefType.Rectangular, UseAutoRelief = true, ReliefRatio = 0.5 };
        SheetBodies = 1;
        var fixedInfo = ((FakeEntity)fixedFace).Info;
        var n = fixedInfo.Normal!;
        var top = Add(ReferenceKind.Face, new EntityInfo { Feature = convert.Info.Name, Geometry = GeometryType.Plane, AreaMm2 = 5000, Normal = n, Origin = fixedInfo.Origin, DistanceMm = 0 });
        var bottom = Add(ReferenceKind.Face, new EntityInfo { Feature = convert.Info.Name, Geometry = GeometryType.Plane, AreaMm2 = 4999, Normal = new Vector3(-n.X, -n.Y, -n.Z) });
        SheetFaceList.Clear();
        SheetFaceList.AddRange(new[] { top, bottom });
        var o = fixedInfo.Origin!;
        bottom.Info.Origin = new Point3(o.X - n.X * spec.ThicknessMm, o.Y - n.Y * spec.ThicknessMm, o.Z - n.Z * spec.ThicknessMm);
        var made = new List<FakeBend>();
        foreach (var edge in bendEdges.Cast<FakeEntity>().Take(ConvertDropsAnEdge ? bendEdges.Count - 1 : bendEdges.Count))
        {
            var (from, to) = FlangeGeometry.Ends(edge.Info.Origin!, edge.Info.Axis!, edge.Info.LengthMm!.Value);
            made.Add(MakeBend($"Bend{FakeBends.Count + 1}", convert.Info.Name!, 90, spec.BendRadiusMm, ConvertAllowance, true, true, from, to));
        }
        var boxBefore = ExactBox;
        if (ExactBox != null && ConvertBoxGrowth != 0) ExactBox = new Box3 { Min = ExactBox.Min, Max = new Point3(ExactBox.Max.X + ConvertBoxGrowth, ExactBox.Max.Y, ExactBox.Max.Z) };
        Undo(convert, () =>
        {
            Unmake(made);
            Entities.Remove(top);
            Entities.Remove(bottom);
            SheetBodies = 0;
            ExactBox = boxBefore;
        });
    }
}
