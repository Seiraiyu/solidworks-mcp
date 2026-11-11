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
