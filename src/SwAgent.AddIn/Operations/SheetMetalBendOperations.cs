using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class SheetMetalSketchedBendOperation : ModelChangeOperation<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>
{
    const string What = "sketched bend";

    /// <summary>SOLIDWORKS' type names a sketched bend may read back as (the live run records which).</summary>
    static readonly string[] Types = { "SM3dBend", "SketchBend" };

    SheetDefaults? _defaults;
    FixedPlane? _fixed;
    int _lines;
    double _angle;
    double _radius;
    BendAllowance? _allowance;

    public SheetMetalSketchedBendOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalSketchedBendRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalSketchedBendRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        double angle = Flanges.Angle(request.AngleDeg, 90, "angle_deg", 180);
        double? radius = request.BendRadiusMm == null ? null : FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a bend");
        var allowance = request.Allowance == null ? null : SheetMetalParts.Allowance(request.Allowance, "allowance");
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var (face, _) = references.Resolve(doc, request.FixedFace, "fixed_face", ReferenceKind.Face);
        var plane = SheetMetalParts.Fixed(doc, face, "fixed_face");
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What);
        var lines = profile.Segments.Where(s => !s.Construction).ToList();
        if (lines.Any(s => s.Kind != SketchSegmentKind.Line || s.Start == null || s.End == null))
            throw FeatureFacts.Invalid("sketch", $"{profile.Name} has more than lines: each line of a sketched bend's sketch is one bend (construction lines are ignored)");
        var o = profile.Origin;
        double off = FeatureFacts.Dot(plane.Normal, new Vector3(o.X - plane.Centre.X, o.Y - plane.Centre.Y, o.Z - plane.Centre.Z));
        if (Math.Abs(FeatureFacts.Dot(profile.Normal, plane.Normal)) < 1 - 1e-6 || Math.Abs(off) > 1e-6)
            throw FeatureFacts.Invalid("sketch", $"{profile.Name} is not on fixed_face's plane: draw the bend lines on the face that stays put (sketch.create on it)");
        var c = plane.Centre;
        var centre = new Point2(
            (c.X - o.X) * profile.XAxis.X + (c.Y - o.Y) * profile.XAxis.Y + (c.Z - o.Z) * profile.XAxis.Z,
            (c.X - o.X) * profile.YAxis.X + (c.Y - o.Y) * profile.YAxis.Y + (c.Z - o.Z) * profile.YAxis.Z);
        for (int i = 0; i < lines.Count; i++)
        {
            double d = Across(lines[i].Start!, lines[i].End!, centre);
            if (d <= 1e-3)
                throw FeatureFacts.Invalid("fixed_face", $"fixed_face's centre lies on the line {lines[i].Name} of {profile.Name}: the side of the lines holding its centre stays put, so it must be off every line");
        }
        _defaults = defaults;
        _fixed = plane;
        _lines = lines.Count;
        _angle = angle;
        _radius = radius ?? defaults.BendRadiusMm;
        _allowance = allowance;
        var spec = new SketchedBendSpec { AngleDeg = angle, RadiusMm = radius, Position = request.Position ?? BendPosition.BendOutside, Flip = request.Flip == true, Allowance = allowance };
        var bend = doc.SheetMetal.SketchedBend(sketch, plane.Centre, spec)
            ?? throw FeatureFacts.NotMade(doc, What, $"from {profile.Name}", "check that its lines cross the face, that no line crosses a flange, and that each bend clears the rest of the part");
        return Flanges.Result(new[] { FeatureFacts.Made(references, doc, bend, tx) });
    }

    /// <summary>How far <paramref name="p"/> is from the line through <paramref name="a"/> and <paramref name="b"/>, mm.</summary>
    static double Across(Point2 a, Point2 b, Point2 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-9 ? Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) : Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / length;
    }

    /// <summary>A bend per line at the angle, radius and allowance asked, the thickness unchanged, the volume as the bend arithmetic folds it, and the fixed face put.</summary>
    protected override void Check(ISwDocument doc, SheetMetalSketchedBendRequest request, SheetMetalFeatureResult result)
    {
        var defaults = _defaults!;
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, Types, What);
        FeatureFacts.MadeFaces(probe, What);
        var bends = Flanges.BendsOf(doc, new[] { result.Feature.Name! });
        if (bends.Count != _lines)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the sketched bend made {bends.Count} bends from {_lines} lines, not one per line",
                new JsonObject { ["check"] = "bends", ["expected"] = _lines, ["measured"] = bends.Count });
        }
        var allowance = _allowance ?? defaults.Allowance;
        Flanges.BendsAre(bends, _angle, _radius, allowance);
        checks.Add(FormattableString.Invariant($"{bends.Count} bends, one per line, at {_angle:0.###}°, inner radius {_radius:0.####} mm and {allowance}"));
        SheetMetalParts.ThicknessIs(doc, defaults.ThicknessMm, checks);
        double after = Volume(doc, doc.SolidBodyCount());
        SheetMetalParts.VolumeDeveloped(BendDevelopment.Develop(SheetMetalParts.Developed(doc, bends), defaults.ThicknessMm, flatten: false), VolumeBefore, after, What, checks);
        SheetMetalParts.StaysPut(doc, _fixed!, What, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }
}

public sealed class SheetMetalFlattenOperation : ModelChangeOperation<SheetMetalFlattenRequest, SheetMetalFlattenResult>
{
    BendVolumeChange? _flattening;
    Vector3? _baseNormal;
    double _thickness;
    bool _unchanged;

    public SheetMetalFlattenOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalFlattenRequest request) => request.Document;

    /// <summary>The bends still folded (with a cylindrical face), as the development checks read them: a bend an Unfold feature already developed adds nothing.</summary>
    static List<DevelopedBend> Folded(ISwDocument doc) =>
        SheetMetalParts.Developed(doc, doc.SheetMetal.Bends().Where(b => SheetMetalParts.Cylinders(doc, b).Count > 0));

    protected override SheetMetalFlattenResult Change(ISwDocument doc, SheetMetalFlattenRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        _thickness = defaults.ThicknessMm;
        _flattening = null;
        _baseNormal = null;
        var flat = doc.Features().FirstOrDefault(f => f.Type == SheetMetalParts.FlatPatternType && f.Parent == null)
            ?? throw new SwAgentException(ErrorCode.OperationFailed, $"{doc.Info().Title} has no Flat-Pattern feature to turn on or off; SOLIDWORKS makes one with every sheet-metal part",
                new JsonObject { ["check"] = "flat_pattern" });
        bool flattened = !flat.Suppressed;
        _unchanged = flattened == request.Flat;
        if (_unchanged) return new SheetMetalFlattenResult { Unchanged = true };
        if (request.Flat)
        {
            _flattening = BendDevelopment.Develop(Folded(doc), _thickness, flatten: true);
            var face = SheetMetalParts.BaseFace(doc);
            if (face != null) _baseNormal = doc.Describe(face).Normal;
        }
        string State(bool suppressed) => suppressed ? "suppressed (folded)" : "unsuppressed (flattened)";
        tx.Edited(new ChangedItem { Name = flat.Name, Property = "suppressed", Before = State(flat.Suppressed), After = State(!request.Flat) }, () =>
        {
            doc.Suppress(flat.Feature, flat.Suppressed);
            doc.Rebuild(false);
        });
        if (!doc.Suppress(flat.Feature, !request.Flat))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not {(request.Flat ? "unsuppress" : "suppress")} {flat.Name}, so the part was not {(request.Flat ? "flattened" : "folded")}");
        if (!doc.Rebuild(false))
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS could not rebuild {doc.Info().Title} {(request.Flat ? "flattened" : "folded")}");
        return new SheetMetalFlattenResult();
    }

    /// <summary>The state reads back; the volume moved as the bend arithmetic develops or folds the bends; flattened, no bend face is left and the sheet is as thin as it is thick.</summary>
    protected override void Check(ISwDocument doc, SheetMetalFlattenRequest request, SheetMetalFlattenResult result)
    {
        var checks = result.Checks;
        var state = SheetMetalParts.State(doc);
        result.State = state;
        result.Unchanged = _unchanged;
        var sheet = doc.SheetMetal;
        result.Flat = SheetMetalParts.Flat(sheet.CutList(), sheet.MmPerUnit(), sheet.Bends().Count);
        string wanted = request.Flat ? "flattened" : "folded";
        if (_unchanged)
        {
            checks.Add($"the part was already {wanted}; nothing was changed");
            return;
        }
        if ((state == SheetState.Flattened) != request.Flat)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the part reads {WireNames.ForEnum(state)} from its Flat-Pattern feature after it was {wanted}",
                new JsonObject { ["check"] = "state", ["expected"] = wanted, ["measured"] = WireNames.ForEnum(state) });
        }
        checks.Add($"the part reads {wanted} from its Flat-Pattern feature");
        double after = Volume(doc, doc.SolidBodyCount());
        if (request.Flat)
        {
            var left = sheet.Bends().Where(b => SheetMetalParts.Cylinders(doc, b).Count > 0).Select(b => b.Name).ToList();
            if (left.Count > 0)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{string.Join(", ", left)} still has cylindrical faces after the part was flattened",
                    new JsonObject { ["check"] = "bend_faces", ["bends"] = left.Select(n => (object?)n).ToList() });
            }
            checks.Add("no bend has a cylindrical face left");
            Thin(doc, checks);
            SheetMetalParts.VolumeDeveloped(_flattening, VolumeBefore, after, "flatten", checks);
        }
        else
        {
            SheetMetalParts.VolumeDeveloped(BendDevelopment.Develop(Folded(doc), _thickness, flatten: false), VolumeBefore, after, "fold", checks);
        }
    }

    /// <summary>Flattened, the part is as thin as the sheet across the base face it had folded, where that face's normal is along x, y or z.</summary>
    void Thin(ISwDocument doc, List<string> checks)
    {
        var box = doc.BoundingBox(true);
        var n = _baseNormal;
        if (box == null || n == null || Flanges.Reach(box, n) == null)
        {
            checks.Add("the flat sheet's thinness was not compared: " + (box == null ? "SOLIDWORKS gave no box" : "its base face's normal is not along x, y or z"));
            return;
        }
        var flip = new Vector3(-n.X, -n.Y, -n.Z);
        double across = Flanges.Reach(box, n)!.Value + Flanges.Reach(box, flip)!.Value;
        FeatureChecks.Near("flat_thickness", "the flattened part's extent across its base face", _thickness, across, 1e-3, "mm");
        checks.Add(FormattableString.Invariant($"the flattened part is {across:0.####} mm across its base face, the sheet's thickness"));
    }
}

/// <summary>What unfold and fold share: the bends named, by name or reference, and the feature SOLIDWORKS made, found by its new name.</summary>
static class Folding
{
    /// <summary>The bends named, each by its name or a reference to it, each once; fields /bends/i.</summary>
    public static List<BendRecord> Named(ReferenceService references, ISwDocument doc, IReadOnlyList<string> texts)
    {
        var all = doc.SheetMetal.Bends();
        var list = new List<BendRecord>();
        for (int i = 0; i < texts.Count; i++)
        {
            string at = $"/bends/{i}";
            var bend = all.FirstOrDefault(b => b.Name == texts[i]);
            if (bend == null)
            {
                var (entity, _) = references.Resolve(doc, texts[i], at, ReferenceKind.Feature);
                string? name = doc.Describe(entity).Name;
                bend = all.FirstOrDefault(b => b.Name == name)
                    ?? throw FeatureFacts.Invalid(at, $"{at} names {name ?? "a feature"}, which is not a bend (sheetmetal.read lists the bends)");
            }
            if (list.Any(b => b.Name == bend.Name)) throw FeatureFacts.Invalid(at, $"{at} names {bend.Name} again");
            list.Add(bend);
        }
        return list;
    }

    /// <summary>The top-level feature of <paramref name="type"/> whose name was not in the tree before; null when SOLIDWORKS made none.</summary>
    public static object? NewFeature(ISwDocument doc, HashSet<string> before, string type) =>
        doc.Features().FirstOrDefault(f => f.Parent == null && f.Type == type && !before.Contains(f.Name))?.Feature;

    public static HashSet<string> Names(ISwDocument doc) => new HashSet<string>(doc.Features().Select(f => f.Name), StringComparer.Ordinal);

    /// <summary>The bends named as SOLIDWORKS reads them now.</summary>
    public static List<BendRecord> Now(ISwDocument doc, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.Ordinal);
        return doc.SheetMetal.Bends().Where(b => wanted.Contains(b.Name)).ToList();
    }
}

public sealed class SheetMetalUnfoldOperation : ModelChangeOperation<SheetMetalUnfoldRequest, SheetMetalFeatureResult>
{
    const string What = "unfold";

    FixedPlane? _fixed;
    List<string> _bends = new List<string>();
    BendVolumeChange? _change;

    public SheetMetalUnfoldOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalUnfoldRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalUnfoldRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        var references = Services.References;
        var (face, _) = references.Resolve(doc, request.FixedFace, "fixed_face", ReferenceKind.Face);
        var plane = SheetMetalParts.Fixed(doc, face, "fixed_face");
        var unfolded = SheetMetalParts.Unfolded(doc);
        var bends = request.Bends == null
            ? doc.SheetMetal.Bends().Where(b => !unfolded.ContainsKey(b.Name)).ToList()
            : Folding.Named(references, doc, request.Bends);
        if (bends.Count == 0) throw FeatureFacts.Invalid("bends", $"{doc.Info().Title} has no folded bend to unfold");
        for (int i = 0; i < bends.Count; i++)
        {
            if (unfolded.TryGetValue(bends[i].Name, out var by))
                throw FeatureFacts.Invalid($"/bends/{i}", $"{bends[i].Name} is already unfolded by {by.Name}; fold it first (sheetmetal.fold)");
        }
        _fixed = plane;
        _bends = bends.Select(b => b.Name).ToList();
        _change = BendDevelopment.Develop(SheetMetalParts.Developed(doc, bends), defaults.ThicknessMm, flatten: true);
        var before = Folding.Names(doc);
        doc.SheetMetal.Unfold(face, bends.Select(b => b.Feature).ToList());
        var made = Folding.NewFeature(doc, before, SheetMetalParts.UnfoldType)
            ?? throw FeatureFacts.NotMade(doc, "unfold", $"of {string.Join(", ", _bends)}", "check that the fixed face is a planar face of the sheet that no bend named runs through");
        return Flanges.Result(new[] { FeatureFacts.Made(references, doc, made, tx) });
    }

    /// <summary>An Unfold whose bends have no cylindrical face left, the fixed face put, and the volume developed as the bend arithmetic says.</summary>
    protected override void Check(ISwDocument doc, SheetMetalUnfoldRequest request, SheetMetalFeatureResult result)
    {
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, SheetMetalParts.UnfoldType, What);
        var bends = Folding.Now(doc, _bends);
        var folded = bends.Where(b => SheetMetalParts.Cylinders(doc, b).Count > 0).Select(b => b.Name).ToList();
        if (folded.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{string.Join(", ", folded)} still has cylindrical faces after the unfold: SOLIDWORKS made {result.Feature.Name} and did not unfold it",
                new JsonObject { ["check"] = "bend_faces", ["bends"] = folded.Select(n => (object?)n).ToList() });
        }
        checks.Add($"{string.Join(", ", _bends)} unfolded: no cylindrical face left");
        SheetMetalParts.StaysPut(doc, _fixed!, What, checks);
        SheetMetalParts.VolumeDeveloped(_change, VolumeBefore, Volume(doc, doc.SolidBodyCount()), What, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }
}

public sealed class SheetMetalFoldOperation : ModelChangeOperation<SheetMetalFoldRequest, SheetMetalFeatureResult>
{
    const string What = "fold";

    FixedPlane? _fixed;
    List<string> _bends = new List<string>();
    double _thickness;

    public SheetMetalFoldOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalFoldRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalFoldRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        var references = Services.References;
        var unfolded = SheetMetalParts.Unfolded(doc);
        if (unfolded.Count == 0) throw FeatureFacts.Invalid("bends", $"nothing is unfolded in {doc.Info().Title}: sheetmetal.fold folds back what sheetmetal.unfold unfolded");
        var bends = request.Bends == null
            ? doc.SheetMetal.Bends().Where(b => unfolded.ContainsKey(b.Name)).ToList()
            : Folding.Named(references, doc, request.Bends);
        for (int i = 0; i < bends.Count; i++)
        {
            if (!unfolded.ContainsKey(bends[i].Name))
                throw FeatureFacts.Invalid($"/bends/{i}", $"{bends[i].Name} is not unfolded, so there is nothing to fold back");
        }
        object face;
        if (request.FixedFace != null) face = references.Resolve(doc, request.FixedFace, "fixed_face", ReferenceKind.Face).Entity;
        else
        {
            var unfold = unfolded[bends[0].Name];
            face = unfold.FixedFace
                ?? throw FeatureFacts.Invalid("fixed_face", $"SOLIDWORKS did not give the face {unfold.Name} kept put; name it in fixed_face");
        }
        var plane = SheetMetalParts.Fixed(doc, face, "fixed_face");
        _fixed = plane;
        _bends = bends.Select(b => b.Name).ToList();
        _thickness = defaults.ThicknessMm;
        var before = Folding.Names(doc);
        doc.SheetMetal.Fold(face, bends.Select(b => b.Feature).ToList());
        var made = Folding.NewFeature(doc, before, SheetMetalParts.FoldType)
            ?? throw FeatureFacts.NotMade(doc, "fold", $"of {string.Join(", ", _bends)}", "check that the fixed face is the one the unfold kept and that what was cut leaves each bend whole enough to fold");
        return Flanges.Result(new[] { FeatureFacts.Made(references, doc, made, tx) });
    }

    /// <summary>A Fold whose bends have their cylindrical faces back, the fixed face put, and the volume folded as the bend arithmetic says.</summary>
    protected override void Check(ISwDocument doc, SheetMetalFoldRequest request, SheetMetalFeatureResult result)
    {
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, SheetMetalParts.FoldType, What);
        var bends = Folding.Now(doc, _bends);
        var flat = bends.Where(b => SheetMetalParts.Cylinders(doc, b).Count == 0).Select(b => b.Name).ToList();
        if (flat.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{string.Join(", ", flat)} has no cylindrical face after the fold: SOLIDWORKS made {result.Feature.Name} and did not fold it",
                new JsonObject { ["check"] = "bend_faces", ["bends"] = flat.Select(n => (object?)n).ToList() });
        }
        checks.Add($"{string.Join(", ", _bends)} folded: their cylindrical faces are back");
        SheetMetalParts.StaysPut(doc, _fixed!, What, checks);
        var change = BendDevelopment.Develop(SheetMetalParts.Developed(doc, bends), _thickness, flatten: false);
        SheetMetalParts.VolumeDeveloped(change, VolumeBefore, Volume(doc, doc.SolidBodyCount()), What, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }
}
