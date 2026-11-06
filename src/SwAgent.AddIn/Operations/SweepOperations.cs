using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class FeatureSweepOperation : ModelChangeOperation<FeatureSweepRequest, FeatureResult>
{
    public FeatureSweepOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureSweepRequest request) => request.Document;

    static string What(FeatureSweepRequest request) => request.Mode == FeatureMode.Cut ? "cut sweep" : "boss sweep";

    protected override FeatureResult Change(ISwDocument doc, FeatureSweepRequest request, ModelTransaction tx, IOperationContext context)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        if (cut && request.Merge != null) throw FeatureFacts.Invalid("merge", "merge is for a boss; a cut always removes material from the bodies it meets");
        var references = Services.References;
        var (profile, _) = references.Resolve(doc, request.Profile, "profile", ReferenceKind.Sketch);
        var contents = FeatureFacts.Profile(doc, profile, "profile", What(request));
        var (path, _) = references.Resolve(doc, request.Path, "path", ReferenceKind.Sketch, ReferenceKind.Edge);
        if (ReferenceEquals(path, profile)) throw FeatureFacts.Invalid("path", "path: the path must be another sketch or an edge, not the profile itself");
        var spec = new SweepSpec { Cut = cut, Path = path, Merge = request.Merge ?? true };
        var guides = FeatureFacts.Entities(references, doc, request.Guides, "guides", ReferenceKind.Sketch);
        for (int i = 0; i < guides.Count; i++)
        {
            if (ReferenceEquals(guides[i], profile) || ReferenceEquals(guides[i], path))
                throw FeatureFacts.Invalid($"/guides/{i}", $"/guides/{i} is the profile or the path; a guide is a sketch of its own");
        }
        spec.Guides.AddRange(guides);
        var feature = doc.Sweep(profile, spec)
            ?? throw FeatureFacts.NotMade(doc, What(request), $"of {contents.Name}", "check that the path starts on the profile's plane and that the profile does not cross itself as it follows the path");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    /// <summary>The feature is a sweep, there is a body, the volume moved the right way, and a boss along one line on an empty part is exactly area × length.</summary>
    protected override void Check(ISwDocument doc, FeatureSweepRequest request, FeatureResult result)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        FeatureFacts.ExpectType(doc, result, cut ? "SweepCut" : "Sweep", What(request));
        int bodies = doc.SolidBodyCount();
        FeatureFacts.HasBody(bodies, What(request));
        double after = Volume(doc, bodies);
        FeatureFacts.VolumeChanged(result, What(request), VolumeBefore, after, cut ? -1 : 1);
        if (!cut && BodiesBefore == 0 && (request.Guides?.Count ?? 0) == 0)
        {
            var references = Services.References;
            var (profile, _) = references.Resolve(doc, request.Profile, "profile", ReferenceKind.Sketch);
            var (path, kind) = references.Resolve(doc, request.Path, "path", ReferenceKind.Sketch, ReferenceKind.Edge);
            var contents = doc.ReadSketch(profile);
            var region = FeatureFacts.Region(contents);
            var line = kind == ReferenceKind.Sketch ? FeatureFacts.OnlyLine(doc.ReadSketch(path)) : null;
            if (region != null && line != null)
            {
                double along = Math.Abs(FeatureFacts.Dot(line.Value.Direction, contents.Normal));
                string why = FormattableString.Invariant($"{region.AreaMm2:0.###} mm² × {along:0.###} mm");
                FeatureChecks.VolumeChangeIs(What(request), region.AreaMm2 * along, after - VolumeBefore, why);
                result.Checks.Add(FormattableString.Invariant($"the volume grew by {after - VolumeBefore:0.###} mm³, the profile's {why} along its normal"));
            }
        }
        FeatureFacts.MadeFaces(result, What(request));
    }
}

public sealed class FeatureLoftOperation : ModelChangeOperation<FeatureLoftRequest, FeatureResult>
{
    public FeatureLoftOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureLoftRequest request) => request.Document;

    static string What(FeatureLoftRequest request) => request.Mode == FeatureMode.Cut ? "cut loft" : "boss loft";

    protected override FeatureResult Change(ISwDocument doc, FeatureLoftRequest request, ModelTransaction tx, IOperationContext context)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        if (cut && request.Merge != null) throw FeatureFacts.Invalid("merge", "merge is for a boss; a cut always removes material from the bodies it meets");
        if (request.Profiles.Count < 2) throw FeatureFacts.Invalid("profiles", "a loft needs at least two profiles, in the order it passes through them");
        var references = Services.References;
        var spec = new LoftSpec { Cut = cut, Merge = request.Merge ?? true };
        spec.Profiles.AddRange(FeatureFacts.Entities(references, doc, request.Profiles, "profiles", ReferenceKind.Sketch));
        for (int i = 0; i < spec.Profiles.Count; i++) FeatureFacts.Profile(doc, spec.Profiles[i], $"/profiles/{i}", What(request));
        var guides = FeatureFacts.Entities(references, doc, request.Guides, "guides", ReferenceKind.Sketch);
        for (int i = 0; i < guides.Count; i++)
        {
            if (spec.Profiles.Any(p => ReferenceEquals(p, guides[i])))
                throw FeatureFacts.Invalid($"/guides/{i}", $"/guides/{i} is one of the profiles; a guide is a sketch of its own");
        }
        spec.Guides.AddRange(guides);
        var feature = doc.Loft(spec)
            ?? throw FeatureFacts.NotMade(doc, What(request), $"through {spec.Profiles.Count} profiles", "check that each profile is closed and on its own plane, and that every guide touches every profile");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    /// <summary>The feature is a loft, there is a body, and the volume moved the right way.</summary>
    protected override void Check(ISwDocument doc, FeatureLoftRequest request, FeatureResult result)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        FeatureFacts.ExpectType(doc, result, cut ? "BlendCut" : "Blend", What(request));
        int bodies = doc.SolidBodyCount();
        FeatureFacts.HasBody(bodies, What(request));
        FeatureFacts.VolumeChanged(result, What(request), VolumeBefore, Volume(doc, bodies), cut ? -1 : 1);
        FeatureFacts.MadeFaces(result, What(request));
    }
}

public sealed class FeatureRibOperation : ModelChangeOperation<FeatureRibRequest, FeatureResult>
{
    public FeatureRibOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureRibRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, FeatureRibRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = new RibSpec
        {
            ThicknessMm = FeatureFacts.Length(request.ThicknessMm, "thickness_mm", "a rib"),
            Side = request.Side ?? RibSide.Both,
            NormalToSketch = request.NormalToSketch == true,
            FlipMaterial = request.FlipMaterial == true,
            DraftOutward = request.DraftOutward == true,
        };
        if (request.DraftDeg != null)
        {
            if (!(request.DraftDeg > 0 && request.DraftDeg < 90)) throw FeatureFacts.Invalid("draft_deg", "draft_deg must be more than 0 and less than 90 degrees");
            spec.DraftDeg = request.DraftDeg.Value;
        }
        else if (request.DraftOutward != null)
        {
            throw FeatureFacts.Invalid("draft_outward", "draft_outward is for a drafted rib; give draft_deg too");
        }
        if (BodiesBefore == 0) throw FeatureFacts.Invalid("sketch", "a rib runs out until it meets a body, and this part has none yet; make the body first");
        var (sketch, _) = Services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var contents = FeatureFacts.Profile(doc, sketch, "sketch", "rib");
        var feature = doc.Rib(sketch, spec)
            ?? throw FeatureFacts.NotMade(doc, "rib", $"from {contents.Name}", "check that the sketch lies on a plane through the body and that the material side reaches it (flip_material)");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The feature is a rib, the volume grew, and it made faces.</summary>
    protected override void Check(ISwDocument doc, FeatureRibRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Rib", "rib");
        FeatureFacts.VolumeChanged(result, "rib", VolumeBefore, Volume(doc, doc.SolidBodyCount()), 1);
        FeatureFacts.MadeFaces(result, "rib");
    }
}
