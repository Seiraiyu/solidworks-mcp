using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>A bend of a <see cref="FakeDocument"/>'s sheet metal, which SetDefaults can change as SOLIDWORKS would.</summary>
sealed class FakeBend
{
    public FakeEntity Feature { get; set; } = null!;
    public string Owner { get; set; } = "";
    public double AngleDeg { get; set; }
    public double RadiusMm { get; set; }
    public BendAllowance Allowance { get; set; } = new BendAllowance { KFactor = 0.5 };
    public bool DefaultRadius { get; set; } = true;
    public bool DefaultAllowance { get; set; } = true;
    public double WidthMm { get; set; }
    public List<FakeEntity> Faces { get; } = new List<FakeEntity>();
}

/// <summary>
/// Phase 6's sheet metal of a <see cref="FakeDocument"/>: a base flange makes Sheet-Metal1, Base-Flange1 and a suppressed
/// Flat-Pattern1, a sheet body with two opposite faces a thickness apart, and a bend with two cylindrical faces at each
/// corner of an open profile; its volume is the bend arithmetic's. The cut list gives the flat size as the straight legs
/// plus each bend's allowance, by width.
/// </summary>
sealed partial class FakeDocument : ISwSheetMetal
{
    public ISwSheetMetal SheetMetal => this;

    /// <summary>The part's defaults, null until a base flange makes it sheet metal.</summary>
    public SheetDefaults? Sheet { get; set; }

    public List<FakeBend> FakeBends { get; } = new List<FakeBend>();

    public List<(object Sketch, BaseFlangeSpec Spec)> BaseFlanges { get; } = new List<(object, BaseFlangeSpec)>();

    public List<SheetDefaultsEdit> DefaultEdits { get; } = new List<SheetDefaultsEdit>();

    /// <summary>How many solid bodies are sheet metal.</summary>
    public int SheetBodies { get; set; }

    /// <summary>The sheet body's faces.</summary>
    public List<FakeEntity> SheetFaceList { get; } = new List<FakeEntity>();

    /// <summary>SetDefaults changes nothing (ModifyDefinition answering true and ignoring it, as measured).</summary>
    public bool DefaultsIgnored { get; set; }

    /// <summary>The bends keep their allowance when the default changes (the global K-factor measured not to move).</summary>
    public bool BendsKeepAllowance { get; set; }

    /// <summary>The thickness reads back this much off what was asked.</summary>
    public double ThicknessError { get; set; }

    /// <summary>The sheet's faces are this much further apart than its thickness.</summary>
    public double FaceGapError { get; set; }

    /// <summary>When set, the cut list returns these instead of the flat size.</summary>
    public List<KeyValuePair<string, string>>? CutListOverride { get; set; }

    /// <summary>The flat pattern's straight legs along its length, mm, and its width.</summary>
    public double FlatStraightMm { get; set; }
    public double FlatWidthMm { get; set; }

    /// <summary>The flat length is this much off the bend arithmetic.</summary>
    public double FlatError { get; set; }

    /// <summary>Added to <see cref="FlatError"/> by each SetDefaults (a flat pattern that moves other than predicted).</summary>
    public double FlatErrorOnChange { get; set; }

    public double UnitMm { get; set; } = 1;

    public SheetDefaults? Defaults() => Sheet == null || !Tree.Any(f => f.Type == SheetMetalParts.SheetMetalType) ? null : new SheetDefaults
    {
        ThicknessMm = Sheet.ThicknessMm + ThicknessError,
        BendRadiusMm = Sheet.BendRadiusMm,
        Allowance = Sheet.Allowance,
        Relief = Sheet.Relief,
        UseAutoRelief = Sheet.UseAutoRelief,
        ReliefTypeCode = Sheet.ReliefTypeCode,
        ReliefRatio = Sheet.ReliefRatio,
    };

    public int SheetBodyCount() => SheetBodies;

    public IReadOnlyList<object> SheetFaces() => SheetFaceList.Where(Entities.Contains).Cast<object>().ToList();

    public IReadOnlyList<BendRecord> Bends() => FakeBends.Where(b => Entities.Contains(b.Feature))
        .Select(b => new BendRecord(b.Feature, b.Feature.Info.Name!, b.Owner, b.AngleDeg, b.RadiusMm, b.Allowance, b.DefaultRadius, b.DefaultAllowance)).ToList();

    public object? BaseFlange(object sketch, BaseFlangeSpec spec)
    {
        BaseFlanges.Add((sketch, spec));
        if (FeatureSelectFails) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch a feature needs");
        if (FeatureRefuses) return null;
        var contents = ReadSketch(sketch);
        var lines = contents.Segments.Where(s => !s.Construction && s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null).Select(s => (s.Start!, s.End!)).ToList();
        var region = FeatureFacts.Region(contents);
        double volume;
        Vector3 normal;
        var corners = new List<double>();
        if (region != null)
        {
            volume = region.AreaMm2 * spec.ThicknessMm;
            normal = contents.Normal;
            var xs = lines.SelectMany(l => new[] { l.Item1.X, l.Item2.X }).ToList();
            var ys = lines.SelectMany(l => new[] { l.Item1.Y, l.Item2.Y }).ToList();
            FlatStraightMm = xs.Count == 0 ? 10 : xs.Max() - xs.Min();
            FlatWidthMm = ys.Count == 0 ? 10 : ys.Max() - ys.Min();
        }
        else
        {
            var chain = BendArithmetic.Chain(lines);
            if (chain == null) return null;
            var open = BendArithmetic.OpenProfile(chain, spec.BendRadiusMm, spec.ThicknessMm, spec.DepthMm);
            double? side = spec.Reverse ? open.RightMm3 : open.LeftMm3;
            if (side == null) return null;
            volume = side.Value;
            corners.AddRange(open.AnglesDeg);
            double arcs = corners.Sum(a => a * Math.PI / 180 * (spec.BendRadiusMm + spec.ThicknessMm / 2));
            FlatStraightMm = volume / (spec.DepthMm * spec.ThicknessMm) - arcs;
            FlatWidthMm = spec.DepthMm;
            var longest = lines.OrderByDescending(l => Math.Pow(l.Item2.X - l.Item1.X, 2) + Math.Pow(l.Item2.Y - l.Item1.Y, 2)).First();
            double dx = longest.Item2.X - longest.Item1.X, dy = longest.Item2.Y - longest.Item1.Y, length = Math.Sqrt(dx * dx + dy * dy);
            var (x, y) = (contents.XAxis, contents.YAxis);
            normal = new Vector3((-dy * x.X + dx * y.X) / length, (-dy * x.Y + dx * y.Y) / length, (-dy * x.Z + dx * y.Z) / length);
        }
        AddFeature("Sheet-Metal1", SheetMetalParts.SheetMetalType);
        var flange = MakeFeature("Base-Flange", "SMBaseFlange", volume, 2);
        AddFeature("Flat-Pattern1", SheetMetalParts.FlatPatternType, suppressed: true);
        Sheet = new SheetDefaults { ThicknessMm = spec.ThicknessMm, BendRadiusMm = spec.BendRadiusMm, Allowance = spec.Allowance, Relief = spec.Relief, UseAutoRelief = true, ReliefRatio = spec.ReliefRatio };
        FlangeK[flange.Info.Name!] = (false, spec.Allowance.KFactor ?? 0.5);
        SheetBodies = 1;
        var top = Add(ReferenceKind.Face, new EntityInfo { Feature = flange.Info.Name, Geometry = GeometryType.Plane, AreaMm2 = 1000, Normal = normal, Origin = new Point3(0, 0, 0) });
        var bottom = Add(ReferenceKind.Face, new EntityInfo { Feature = flange.Info.Name, Geometry = GeometryType.Plane, AreaMm2 = 999, Normal = new Vector3(-normal.X, -normal.Y, -normal.Z) });
        SheetFaceList.AddRange(new[] { top, bottom });
        Place(bottom, spec.ThicknessMm, normal);
        for (int i = 0; i < corners.Count; i++)
        {
            var bend = new FakeBend
            {
                Feature = AddFeature($"BaseBend{FakeBends.Count + 1}", "OneBend", parent: flange.Info.Name),
                Owner = flange.Info.Name!,
                AngleDeg = corners[i],
                RadiusMm = spec.BendRadiusMm,
                Allowance = spec.Allowance,
                WidthMm = spec.DepthMm,
            };
            var axis = contents.Normal;
            var box = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(axis.X * spec.DepthMm, axis.Y * spec.DepthMm, axis.Z * spec.DepthMm) };
            bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = flange.Info.Name, Geometry = GeometryType.Cylinder, RadiusMm = spec.BendRadiusMm, Axis = axis, Box = box }));
            bend.Faces.Add(Add(ReferenceKind.Face, new EntityInfo { Feature = flange.Info.Name, Geometry = GeometryType.Cylinder, RadiusMm = spec.BendRadiusMm + spec.ThicknessMm, Axis = axis, Box = box }));
            _facesOf[bend.Feature] = bend.Faces;
            FakeBends.Add(bend);
        }
        Undo(flange, () =>
        {
            foreach (var b in FakeBends.Where(b => b.Owner == flange.Info.Name).ToList())
            {
                RemoveFeature(b.Feature);
                Entities.RemoveAll(b.Faces.Contains);
                FakeBends.Remove(b);
            }
            Entities.Remove(top);
            Entities.Remove(bottom);
            SheetBodies = 0;
        });
        return flange;
    }

    /// <summary>Puts the face opposite the sheet's base face its thickness (and any gap error) away along the normal.</summary>
    void Place(FakeEntity bottom, double thicknessMm, Vector3 normal)
    {
        double d = thicknessMm + FaceGapError;
        bottom.Info.Origin = new Point3(normal.X * d, normal.Y * d, normal.Z * d);
    }

    public void SetDefaults(SheetDefaultsEdit edit)
    {
        DefaultEdits.Add(edit);
        if (DefaultsIgnored || Sheet == null) return;
        FlatError += FlatErrorOnChange;
        if (edit.ThicknessMm != null)
        {
            double ratio = edit.ThicknessMm.Value / Sheet.ThicknessMm;
            var flange = (FakeEntity)FeatureByName("Base-Flange1")!;
            double before = _volumeOf[flange];
            _volumeOf[flange] = before * ratio;
            MassProperties!.VolumeMm3 += before * (ratio - 1);
            Sheet.ThicknessMm = edit.ThicknessMm.Value;
            var top = SheetFaceList[0];
            Place(SheetFaceList[1], Sheet.ThicknessMm, top.Info.Normal!);
            foreach (var b in FakeBends) b.Faces[1].Info.RadiusMm = b.RadiusMm + Sheet.ThicknessMm;
        }
        if (edit.BendRadiusMm != null)
        {
            Sheet.BendRadiusMm = edit.BendRadiusMm.Value;
            foreach (var b in FakeBends.Where(b => b.DefaultRadius))
            {
                b.RadiusMm = edit.BendRadiusMm.Value;
                b.Faces[0].Info.RadiusMm = b.RadiusMm;
                b.Faces[1].Info.RadiusMm = b.RadiusMm + Sheet.ThicknessMm;
            }
        }
        if (edit.Allowance != null)
        {
            Sheet.Allowance = edit.Allowance;
            if (!BendsKeepAllowance)
            {
                foreach (var b in FakeBends.Where(b => b.DefaultAllowance)) b.Allowance = edit.Allowance;
            }
        }
        if (edit.Relief != null)
        {
            Sheet.Relief = edit.Relief;
            Sheet.UseAutoRelief = true;
        }
        else if (edit.UseAutoRelief != null)
        {
            Sheet.UseAutoRelief = edit.UseAutoRelief.Value;
            Sheet.Relief = null;
            if (edit.ReliefTypeCode != null) Sheet.ReliefTypeCode = edit.ReliefTypeCode.Value;
        }
        if (edit.ReliefRatio != null) Sheet.ReliefRatio = edit.ReliefRatio.Value;
    }

    /// <summary>Each base flange's own K-factor override switch and K-factor.</summary>
    public Dictionary<string, (bool Override, double K)> FlangeK { get; } = new Dictionary<string, (bool, double)>();

    /// <summary>A K-factor written into a base flange does not reach its bends either.</summary>
    public bool FlangeKIgnored { get; set; }

    public IReadOnlyList<FlangeKFactorWrite> KFactorToBaseFlanges(double k, double? oldK)
    {
        var written = new List<FlangeKFactorWrite>();
        foreach (var flange in FlangeK.Keys.Where(n => FeatureByName(n) != null).ToList())
        {
            var missed = FakeBends.Where(b => b.Owner == flange && Entities.Contains(b.Feature)
                    && (b.DefaultAllowance || (oldK != null && b.Allowance.KFactor != null && Math.Abs(b.Allowance.KFactor.Value - oldK.Value) <= 1e-6))
                    && (b.Allowance.KFactor == null || Math.Abs(b.Allowance.KFactor.Value - k) > 1e-9))
                .ToList();
            if (missed.Count == 0) continue;
            var (overrideBefore, kBefore) = FlangeK[flange];
            written.Add(new FlangeKFactorWrite(flange, overrideBefore, kBefore, missed.Select(b => $"{b.Feature.Info.Name} ({b.Allowance})").ToList(), true));
            SetBaseFlangeKFactor(flange, true, k);
        }
        return written;
    }

    public void SetBaseFlangeKFactor(string flange, bool overrideKFactor, double kFactor)
    {
        if (!FlangeK.ContainsKey(flange)) throw new SwAgentException(ErrorCode.ApiError, $"the part has no base flange {flange} to put back");
        FlangeK[flange] = (overrideKFactor, kFactor);
        if (FlangeKIgnored || Sheet == null) return;
        // Override on: the bends read the flange's K. Off (a rollback): they go back to what they read before, its K here.
        var allowance = new BendAllowance { KFactor = kFactor };
        foreach (var b in FakeBends.Where(b => b.Owner == flange && b.DefaultAllowance)) b.Allowance = allowance;
    }

    public IReadOnlyList<KeyValuePair<string, string>> CutList()
    {
        if (CutListOverride != null) return CutListOverride;
        if (Sheet == null) return new List<KeyValuePair<string, string>>();
        double length = FlatStraightMm + FakeBends.Sum(b => BendArithmetic.AllowanceMm(b.AngleDeg, b.RadiusMm, Sheet.ThicknessMm, b.Allowance)) + FlatError;
        string N(double v) => (v / UnitMm).ToString("F6", CultureInfo.InvariantCulture);
        return new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(SheetMetalParts.LengthProperty, N(Math.Max(length, FlatWidthMm))),
            new KeyValuePair<string, string>(SheetMetalParts.WidthProperty, N(Math.Min(length, FlatWidthMm))),
            new KeyValuePair<string, string>("Sheet Metal Thickness", N(Sheet.ThicknessMm)),
            new KeyValuePair<string, string>("Bends", FakeBends.Count.ToString(CultureInfo.InvariantCulture)),
        };
    }

    public double MmPerUnit() => UnitMm;
}
