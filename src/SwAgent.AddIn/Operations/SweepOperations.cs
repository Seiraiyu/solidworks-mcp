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
