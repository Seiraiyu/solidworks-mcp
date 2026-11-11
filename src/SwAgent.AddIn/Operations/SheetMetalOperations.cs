using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class SheetMetalBaseFlangeOperation : ModelChangeOperation<SheetMetalBaseFlangeRequest, SheetMetalFeatureResult>
{
    const string What = "base flange";

    BaseFlangeSpec? _spec;
    SketchContents? _profile;

    public SheetMetalBaseFlangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalBaseFlangeRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalBaseFlangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        _spec = null;
        _profile = null;
        var spec = new BaseFlangeSpec
        {
            ThicknessMm = FeatureFacts.Length(request.ThicknessMm, "thickness_mm", "a base flange"),
            BendRadiusMm = FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a base flange"),
            Allowance = request.Allowance == null ? new BendAllowance { KFactor = 0.5 } : SheetMetalParts.Allowance(request.Allowance, "allowance"),
            Reverse = request.Reverse == true,
            Relief = request.Relief ?? ReliefType.Rectangular,
            ReliefRatio = SheetMetalParts.ReliefRatio(request.ReliefRatio, "relief_ratio"),
        };
        if (SheetMetalParts.IsSheetMetal(doc) || doc.SheetMetal.SheetBodyCount() > 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{doc.Info().Title} already has a sheet-metal body, and a part has one: its thickness belongs to the part, so a second body would change the first. Add flanges to it instead, or start a new part. Nothing was changed",
                new JsonObject { ["field"] = "document" });
        }
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What);
        bool open = Open(profile);
        if (open) spec.DepthMm = FeatureFacts.Length(request.DepthMm, "depth_mm", "a base flange from an open profile");
        else if (request.DepthMm != null) throw FeatureFacts.Invalid("depth_mm", $"depth_mm is for an open profile, and {profile.Name} is closed: a closed profile makes a flat tab as thick as thickness_mm");
        var before = new HashSet<string>(doc.Features().Select(f => f.Name), StringComparer.Ordinal);
        var flange = doc.SheetMetal.BaseFlange(sketch, spec)
            ?? throw FeatureFacts.NotMade(doc, What, $"from {profile.Name}", "check that its profile is one closed loop or one open chain, and that the sheet fits its corners at this radius");
        foreach (var made in doc.Features().Where(f => f.Parent == null && !before.Contains(f.Name) && (f.Type == SheetMetalParts.SheetMetalType || f.Type == SheetMetalParts.FlatPatternType)))
        {
            string name = made.Name;
            tx.Made(new CreatedItem { Name = name, Kind = ReferenceKind.Feature, Reference = references.Describe(doc, made.Feature).Reference }, () =>
            {
                if (doc.FeatureByName(name) is object left)
                {
                    doc.Delete(left, children: false, absorbed: false);
                    doc.Rebuild(false);
                }
            });
        }
        var result = FeatureFacts.Made(references, doc, flange, tx);
        _spec = spec;
        _profile = profile;
        return new SheetMetalFeatureResult { Feature = result.Feature, Faces = result.Faces };
    }

    /// <summary>A profile is open when an end of one of its drawn segments meets no other.</summary>
    static bool Open(SketchContents profile)
    {
        var ends = profile.Segments.Where(s => !s.Construction && s.Start != null && s.End != null).SelectMany(s => new[] { s.Start!, s.End! }).ToList();
        return ends.Any(p => ends.Count(q => Math.Abs(p.X - q.X) <= 1e-6 && Math.Abs(p.Y - q.Y) <= 1e-6) == 1);
    }

    /// <summary>The part is sheet metal with one sheet body, its thickness, radius and allowance read back, and its volume and bends are the profile's.</summary>
    protected override void Check(ISwDocument doc, SheetMetalBaseFlangeRequest request, SheetMetalFeatureResult result)
    {
        var spec = _spec!;
        var profile = _profile!;
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, "SMBaseFlange", What);
        int sheets = doc.SheetMetal.SheetBodyCount();
        bool flat = doc.Features().Any(f => f.Type == SheetMetalParts.FlatPatternType);
        if (!SheetMetalParts.IsSheetMetal(doc) || !flat || sheets != 1)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the part is not one sheet-metal body after the base flange: SheetMetal feature {(SheetMetalParts.IsSheetMetal(doc) ? "present" : "missing")}, Flat-Pattern {(flat ? "present" : "missing")}, {sheets} sheet bodies",
                new JsonObject { ["check"] = "sheet_metal", ["sheet_bodies"] = sheets });
        }
        SheetMetalParts.ThicknessIs(doc, spec.ThicknessMm, checks);
        var defaults = doc.SheetMetal.Defaults()!;
        FeatureChecks.Near("bend_radius", "the part's default bend radius as SOLIDWORKS reads it back", spec.BendRadiusMm, defaults.BendRadiusMm, 1e-6, "mm");
        if (!SheetMetalParts.Same(spec.Allowance, defaults.Allowance))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the part's default allowance reads back as {defaults.Allowance}, not the {spec.Allowance} asked",
                new JsonObject { ["check"] = "allowance", ["expected"] = spec.Allowance.ToString(), ["measured"] = defaults.Allowance.ToString() });
        }
        checks.Add(FormattableString.Invariant($"the default bend radius reads back as {defaults.BendRadiusMm:0.####} mm and the allowance as {defaults.Allowance}"));
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureFacts.VolumeChanged(probe, What, VolumeBefore, after, 1);
        var bends = doc.SheetMetal.Bends().Where(b => b.Owner == result.Feature.Name).ToList();
        if (spec.DepthMm == 0) Closed(profile, spec, bends, after - VolumeBefore, checks);
        else Opened(profile, spec, bends, after - VolumeBefore, checks);
        FeatureFacts.MadeFaces(probe, What);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }

    /// <summary>A flat tab: no bends, and exactly the profile's area times the thickness wherever the profile can be measured.</summary>
    void Closed(SketchContents profile, BaseFlangeSpec spec, IReadOnlyList<BendRecord> bends, double added, List<string> checks)
    {
        if (bends.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"a base flange from a closed profile is flat, and this one has {bends.Count} bends",
                new JsonObject { ["check"] = "bends", ["expected"] = 0, ["measured"] = bends.Count });
        }
        var region = FeatureFacts.Region(profile);
        if (region == null || BodiesBefore > 0) return;
        string why = FormattableString.Invariant($"{region.AreaMm2:0.###} mm² × {spec.ThicknessMm:0.###} mm");
        FeatureChecks.VolumeChangeIs(What, region.AreaMm2 * spec.ThicknessMm, added, why);
        checks.Add(FormattableString.Invariant($"the volume grew by {added:0.###} mm³, the profile's {why}"));
    }

    /// <summary>An open chain of lines: a bend at each corner at the radius asked, and the volume the bend arithmetic gives on one side or the other.</summary>
    void Opened(SketchContents profile, BaseFlangeSpec spec, IReadOnlyList<BendRecord> bends, double added, List<string> checks)
    {
        var lines = profile.Segments.Where(s => !s.Construction).ToList();
        var chain = lines.All(s => s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null)
            ? BendArithmetic.Chain(lines.Select(s => (s.Start!, s.End!)).ToList())
            : null;
        if (chain == null)
        {
            checks.Add($"{profile.Name} is not one chain of lines, so only the volume's growth and the {bends.Count} bends were checked");
            return;
        }
        var open = BendArithmetic.OpenProfile(chain, spec.BendRadiusMm, spec.ThicknessMm, spec.DepthMm);
        if (bends.Count != open.Bends)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the open profile has {open.Bends} corners, and the base flange made {bends.Count} bends",
                new JsonObject { ["check"] = "bends", ["expected"] = open.Bends, ["measured"] = bends.Count });
        }
        var expected = open.AnglesDeg.OrderBy(a => a).ToList();
        var measured = bends.Select(b => b.AngleDeg).OrderBy(a => a).ToList();
        for (int i = 0; i < expected.Count; i++)
            FeatureChecks.Near("bend_angle", $"bend {i + 1}'s angle", expected[i], measured[i], 1e-3, "°");
        foreach (var b in bends) FeatureChecks.Near("bend_radius", $"{b.Name}'s inner radius", spec.BendRadiusMm, b.RadiusMm, 1e-6, "mm");
        if (BodiesBefore > 0) return;
        double? side = new[] { open.LeftMm3, open.RightMm3 }.Where(v => v != null).OrderBy(v => Math.Abs(v!.Value - added)).FirstOrDefault();
        string why = FormattableString.Invariant($"{chain.Count - 1} legs and {open.Bends} bends of R {spec.BendRadiusMm:0.###} mm, {spec.ThicknessMm:0.###} mm thick, {spec.DepthMm:0.###} mm deep");
        FeatureChecks.VolumeChangeIs(What, side ?? 0, added, why);
        string which = side == open.LeftMm3 ? "left" : "right";
        checks.Add(FormattableString.Invariant($"the volume grew by {added:0.###} mm³, the bend arithmetic's for {why}, with the material on the chain's {which}; {open.Bends} bends at the corners' angles"));
    }
}

public sealed class SheetMetalReadOperation : Operation<SheetMetalReadRequest, SheetMetalReadResult>
{
    readonly AddInServices _services;

    public SheetMetalReadOperation(AddInServices services)
    {
        _services = services;
    }

    protected override SheetMetalReadResult Run(SheetMetalReadRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "sheetmetal.read");
        var result = new SheetMetalReadResult { Document = doc.Info() };
        var defaults = SheetMetalParts.IsSheetMetal(doc) ? doc.SheetMetal.Defaults() : null;
        if (defaults == null) return result;
        var sheet = doc.SheetMetal;
        var references = _services.References;
        result.SheetMetal = true;
        result.State = SheetMetalParts.State(doc);
        result.ThicknessMm = defaults.ThicknessMm;
        result.BendRadiusMm = defaults.BendRadiusMm;
        result.Allowance = defaults.Allowance;
        result.Relief = defaults.Relief;
        result.ReliefRatio = defaults.ReliefRatio;
        result.SheetBodies = sheet.SheetBodyCount();
        result.OtherBodies = Math.Max(0, doc.SolidBodyCount() - result.SheetBodies);
        result.Bends = SheetMetalParts.Describe(references, doc, sheet.Bends());
        var face = SheetMetalParts.BaseFace(doc);
        if (face != null) result.BaseFace = references.Describe(doc, face);
        var cutList = sheet.CutList();
        result.CutList = cutList.Select(p => $"{p.Key} = {p.Value}").ToList();
        result.Flat = SheetMetalParts.Flat(cutList, sheet.MmPerUnit(), result.Bends.Count);
        return result;
    }

    /// <summary>Read again, the part has the same thickness, state and bends.</summary>
    protected override void Verify(SheetMetalReadRequest request, SheetMetalReadResult result, IOperationContext context)
    {
        if (!result.SheetMetal) return;
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var defaults = doc.SheetMetal.Defaults();
        var bends = doc.SheetMetal.Bends().Select(b => b.Name).ToList();
        if (defaults == null || Math.Abs(defaults.ThicknessMm - result.ThicknessMm!.Value) > 1e-9 || SheetMetalParts.State(doc) != result.State
            || !bends.SequenceEqual(result.Bends.Select(b => b.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the sheet metal changed while it was read; ask again");
    }
}

public sealed class SheetMetalSetParametersOperation : ModelChangeOperation<SheetMetalSetParametersRequest, SheetMetalSetParametersResult>
{
    SheetDefaults? _before;
    List<BendRecord> _bendsBefore = new List<BendRecord>();
    HashSet<string> _followRadius = new HashSet<string>(StringComparer.Ordinal);
    HashSet<string> _followAllowance = new HashSet<string>(StringComparer.Ordinal);
    IReadOnlyList<KeyValuePair<string, string>> _cutListBefore = new List<KeyValuePair<string, string>>();
    SheetDefaultsEdit _edit = new SheetDefaultsEdit();

    public SheetMetalSetParametersOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalSetParametersRequest request) => request.Document;

    protected override SheetMetalSetParametersResult Change(ISwDocument doc, SheetMetalSetParametersRequest request, ModelTransaction tx, IOperationContext context)
    {
        var before = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        if (request.ThicknessMm == null && request.BendRadiusMm == null && request.Allowance == null && request.Relief == null && request.ReliefRatio == null)
            throw FeatureFacts.Invalid("thickness_mm", "give at least one of thickness_mm, bend_radius_mm, allowance, relief or relief_ratio to change");
        var edit = new SheetDefaultsEdit
        {
            ThicknessMm = request.ThicknessMm == null ? null : FeatureFacts.Length(request.ThicknessMm, "thickness_mm", "a sheet"),
            BendRadiusMm = request.BendRadiusMm == null ? null : FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a bend"),
            Allowance = request.Allowance == null ? null : SheetMetalParts.Allowance(request.Allowance, "allowance"),
            Relief = request.Relief,
            ReliefRatio = request.ReliefRatio == null ? null : SheetMetalParts.ReliefRatio(request.ReliefRatio, "relief_ratio"),
        };
        var sheet = doc.SheetMetal;
        _before = before;
        _edit = edit;
        _bendsBefore = sheet.Bends().ToList();
        _followRadius = new HashSet<string>(_bendsBefore.Where(b => b.DefaultRadius || Math.Abs(b.RadiusMm - before.BendRadiusMm) <= 1e-6).Select(b => b.Name), StringComparer.Ordinal);
        _followAllowance = new HashSet<string>(_bendsBefore.Where(b => b.DefaultAllowance || SheetMetalParts.Same(b.Allowance, before.Allowance)).Select(b => b.Name), StringComparer.Ordinal);
        _cutListBefore = sheet.CutList();
        string owner = doc.Features().First(f => f.Type == SheetMetalParts.SheetMetalType).Name;
        void Record(string property, string was, string now, SheetDefaultsEdit back) =>
            tx.Edited(new ChangedItem { Name = owner, Property = property, Before = was, After = now }, () =>
            {
                sheet.SetDefaults(back);
                doc.Rebuild(false);
            });
        string Mm(double v) => FormattableString.Invariant($"{v:0.####} mm");
        if (edit.ThicknessMm != null) Record("thickness", Mm(before.ThicknessMm), Mm(edit.ThicknessMm.Value), new SheetDefaultsEdit { ThicknessMm = before.ThicknessMm });
        if (edit.BendRadiusMm != null) Record("bend_radius", Mm(before.BendRadiusMm), Mm(edit.BendRadiusMm.Value), new SheetDefaultsEdit { BendRadiusMm = before.BendRadiusMm });
        if (edit.Allowance != null) Record("allowance", before.Allowance.ToString(), edit.Allowance.ToString(), new SheetDefaultsEdit { Allowance = before.Allowance });
        if (edit.Relief != null) Record("relief", before.Relief == null ? "none" : WireNames.ForEnum(before.Relief.Value), WireNames.ForEnum(edit.Relief.Value), new SheetDefaultsEdit { Relief = before.Relief ?? ReliefType.Rectangular });
        if (edit.ReliefRatio != null) Record("relief_ratio", FormattableString.Invariant($"{before.ReliefRatio:0.####}"), FormattableString.Invariant($"{edit.ReliefRatio:0.####}"), new SheetDefaultsEdit { ReliefRatio = before.ReliefRatio });
        sheet.SetDefaults(edit);
        if (!doc.Rebuild(false)) context.Log.Warn($"{Name}: SOLIDWORKS reported the rebuild after the change failed; the checks read the part as it is");
        return new SheetMetalSetParametersResult { FlatBefore = SheetMetalParts.Flat(_cutListBefore, sheet.MmPerUnit(), _bendsBefore.Count) };
    }

    /// <summary>Every value reads back, the geometry measures it, every bend that follows the default took it, and the flat size and volume moved as predicted.</summary>
    protected override void Check(ISwDocument doc, SheetMetalSetParametersRequest request, SheetMetalSetParametersResult result)
    {
        var before = _before!;
        var edit = _edit;
        var checks = result.Checks;
        var sheet = doc.SheetMetal;
        var now = sheet.Defaults() ?? throw new SwAgentException(ErrorCode.VerificationFailed, "the part is no longer sheet metal after the change", new JsonObject { ["check"] = "sheet_metal" });
        double thickness = edit.ThicknessMm ?? before.ThicknessMm;
        SheetMetalParts.ThicknessIs(doc, thickness, checks);
        if (edit.BendRadiusMm != null) FeatureChecks.Near("bend_radius", "the part's default bend radius as SOLIDWORKS reads it back", edit.BendRadiusMm.Value, now.BendRadiusMm, 1e-6, "mm");
        if (edit.Allowance != null && !SheetMetalParts.Same(edit.Allowance, now.Allowance))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the part's default allowance reads back as {now.Allowance}, not the {edit.Allowance} asked",
                new JsonObject { ["check"] = "allowance", ["expected"] = edit.Allowance.ToString(), ["measured"] = now.Allowance.ToString() });
        }
        if (edit.Relief != null && now.Relief != edit.Relief)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the part's relief reads back as {(now.Relief == null ? "none" : WireNames.ForEnum(now.Relief.Value))}, not {WireNames.ForEnum(edit.Relief.Value)}",
                new JsonObject { ["check"] = "relief", ["expected"] = WireNames.ForEnum(edit.Relief.Value) });
        }
        if (edit.ReliefRatio != null) FeatureChecks.Near("relief_ratio", "the relief ratio as SOLIDWORKS reads it back", edit.ReliefRatio.Value, now.ReliefRatio, 1e-6, "");
        var bends = sheet.Bends().ToList();
        Followed(doc, bends, edit, thickness, checks);
        double after = Volume(doc, doc.SolidBodyCount());
        var cutList = sheet.CutList();
        result.FlatAfter = SheetMetalParts.Flat(cutList, sheet.MmPerUnit(), bends.Count);
        if (edit.ThicknessMm == null && edit.BendRadiusMm == null)
        {
            FeatureChecks.VolumeChanged(Name, VolumeBefore, after, 0);
            checks.Add(FormattableString.Invariant($"the folded volume stayed at {after:0.###} mm³"));
            if (edit.Allowance != null) FlatMoved(doc, bends, edit.Allowance, thickness, result, cutList, checks);
        }
        else
        {
            if (edit.ThicknessMm != null && bends.Count == 0 && BodiesBefore == 1)
            {
                double ratio = edit.ThicknessMm.Value / before.ThicknessMm;
                string why = FormattableString.Invariant($"a flat sheet {before.ThicknessMm:0.####} -> {edit.ThicknessMm:0.####} mm thick");
                FeatureChecks.VolumeChangeIs(Name, VolumeBefore * (ratio - 1), after - VolumeBefore, why);
                checks.Add(FormattableString.Invariant($"the volume went {VolumeBefore:0.###} -> {after:0.###} mm³, {why}"));
            }
            if (result.FlatBefore != null && result.FlatAfter != null)
                checks.Add(FormattableString.Invariant($"the flat size went {result.FlatBefore.LengthMm:0.###} × {result.FlatBefore.WidthMm:0.###} -> {result.FlatAfter.LengthMm:0.###} × {result.FlatAfter.WidthMm:0.###} mm (not predicted: the flanges' lengths hold where they were measured to)"));
        }
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }

    /// <summary>Every bend that followed the default before reads back the new radius and allowance, and its cylindrical faces measure the radius.</summary>
    void Followed(ISwDocument doc, IReadOnlyList<BendRecord> bends, SheetDefaultsEdit edit, double thickness, List<string> checks)
    {
        int radii = 0, allowances = 0;
        foreach (var bend in bends)
        {
            if (edit.BendRadiusMm != null && _followRadius.Contains(bend.Name))
            {
                FeatureChecks.Near("bend_follows", $"{bend.Name}'s inner radius, which used the part's default", edit.BendRadiusMm.Value, bend.RadiusMm, 1e-6, "mm");
                var cylinders = doc.FacesOf(bend.Feature).Select(doc.Describe).Where(i => i.Geometry == GeometryType.Cylinder && i.RadiusMm != null).ToList();
                if (cylinders.Count > 0)
                    FeatureChecks.Near("bend_radius_measured", $"{bend.Name}'s inner cylindrical face's radius", edit.BendRadiusMm.Value, cylinders.Min(i => i.RadiusMm!.Value), 1e-4, "mm");
                radii++;
            }
            if (edit.Allowance != null && _followAllowance.Contains(bend.Name))
            {
                if (!SheetMetalParts.Same(edit.Allowance, bend.Allowance))
                {
                    throw new SwAgentException(ErrorCode.VerificationFailed,
                        $"{bend.Name} used the part's default allowance and SOLIDWORKS kept it at {bend.Allowance}, not the {edit.Allowance} asked",
                        new JsonObject { ["check"] = "bend_follows", ["bend"] = bend.Name, ["expected"] = edit.Allowance.ToString(), ["measured"] = bend.Allowance.ToString() });
                }
                allowances++;
            }
        }
        if (edit.BendRadiusMm != null) checks.Add(FormattableString.Invariant($"{radii} bends that used the default radius read back and measure {edit.BendRadiusMm:0.####} mm"));
        if (edit.Allowance != null) checks.Add($"{allowances} bends that used the default allowance read back {edit.Allowance}");
    }

    /// <summary>
    /// An allowance-only change leaves the straight legs as they are, so the flat size's length plus width moves by the
    /// change in each following bend's allowance; checked when every bend is square to the others (so each lies along
    /// one side of the flat rectangle) and both sizes are known, within the cut list's precision.
    /// </summary>
    void FlatMoved(ISwDocument doc, IReadOnlyList<BendRecord> bends, BendAllowance allowance, double thickness, SheetMetalSetParametersResult result, IReadOnlyList<KeyValuePair<string, string>> cutList, List<string> checks)
    {
        var flatBefore = result.FlatBefore;
        var flatAfter = result.FlatAfter;
        if (flatBefore == null || flatAfter == null || bends.Count == 0)
        {
            checks.Add("the flat size was not compared: " + (bends.Count == 0 ? "the part has no bends" : "the cut list did not give it as numbers"));
            return;
        }
        var axes = bends.Select(b => doc.FacesOf(b.Feature).Select(doc.Describe).FirstOrDefault(i => i.Geometry == GeometryType.Cylinder && i.Axis != null)?.Axis).ToList();
        bool square = axes.All(a => a != null) && axes.All(a => axes.All(b => { double d = Math.Abs(FeatureFacts.Dot(a!, b!)); return d < 1e-6 || d > 1 - 1e-6; }));
        if (!square)
        {
            checks.Add("the flat size was not compared: the bends are not square to each other");
            return;
        }
        double predicted = bends.Where(b => _followAllowance.Contains(b.Name)).Sum(b =>
        {
            var old = _bendsBefore.First(x => x.Name == b.Name);
            return BendArithmetic.AllowanceMm(b.AngleDeg, b.RadiusMm, thickness, allowance) - BendArithmetic.AllowanceMm(old.AngleDeg, old.RadiusMm, thickness, old.Allowance);
        });
        double measured = flatAfter.LengthMm + flatAfter.WidthMm - flatBefore.LengthMm - flatBefore.WidthMm;
        double tolerance = SheetMetalParts.FlatRoundingMm(cutList, doc.SheetMetal.MmPerUnit()) + SheetMetalParts.FlatRoundingMm(_cutListBefore, doc.SheetMetal.MmPerUnit()) + 1e-9;
        FeatureChecks.Near("flat_size", "how far the flat size's length plus width moved", predicted, measured, tolerance, "mm");
        checks.Add(FormattableString.Invariant($"the flat size's length plus width moved {measured:0.####} mm, the bend arithmetic's {predicted:0.####} mm within the cut list's {tolerance:0.####} mm"));
    }
}
