using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class FeatureExtrudeOperation : ModelChangeOperation<FeatureExtrudeRequest, FeatureResult>
{
    public FeatureExtrudeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureExtrudeRequest request) => request.Document;

    static string What(FeatureExtrudeRequest request) => request.Mode == FeatureMode.Cut ? "cut extrude" : "boss extrude";

    protected override FeatureResult Change(ISwDocument doc, FeatureExtrudeRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = Spec(request);
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        if (request.UpTo != null) spec.UpTo = UpTo(doc, request.UpTo, spec.End, "up_to");
        if (request.Direction2?.UpTo != null) spec.Direction2!.UpTo = UpTo(doc, request.Direction2.UpTo, spec.Direction2.End, "/direction2/up_to");
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What(request));
        var feature = doc.Extrude(sketch, spec)
            ?? throw FeatureFacts.NotMade(doc, What(request), $"from {profile.Name}", "check that its profile is closed and that the end can be reached");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    object UpTo(ISwDocument doc, string text, ExtrudeEnd end, string field) => end == ExtrudeEnd.UpToVertex
        ? Services.References.Resolve(doc, text, field, ReferenceKind.Vertex, ReferenceKind.Point).Entity
        : Services.References.Resolve(doc, text, field, ReferenceKind.Face, ReferenceKind.Plane).Entity;

    /// <summary>The feature is an extrusion or a cut, the volume moved the right way, and a blind boss on an empty part added exactly area × depth.</summary>
    protected override void Check(ISwDocument doc, FeatureExtrudeRequest request, FeatureResult result)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        FeatureFacts.ExpectType(doc, result, cut ? "Cut" : "Extrusion", What(request));
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureFacts.VolumeChanged(result, What(request), VolumeBefore, after, cut ? -1 : 1);
        var end = request.End ?? ExtrudeEnd.Blind;
        var second = request.Direction2;
        bool blindSecond = second == null || ((second.End ?? ExtrudeEnd.Blind) == ExtrudeEnd.Blind && second.DraftDeg == null);
        bool exact = !cut && (end == ExtrudeEnd.Blind || end == ExtrudeEnd.MidPlane) && (request.DraftDeg ?? 0) == 0 && request.Thin == null && blindSecond && BodiesBefore == 0;
        if (exact)
        {
            var (sketch, _) = Services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
            var region = FeatureFacts.Region(doc.ReadSketch(sketch));
            if (region != null)
            {
                double depth = request.DepthMm!.Value + (second?.DepthMm ?? 0);
                string why = FormattableString.Invariant($"{region.AreaMm2:0.###} mm² × {depth:0.###} mm");
                FeatureChecks.VolumeChangeIs(What(request), region.AreaMm2 * depth, after - VolumeBefore, why);
                result.Checks.Add(FormattableString.Invariant($"the volume grew by {after - VolumeBefore:0.###} mm³, the profile's {why}"));
            }
        }
        FeatureFacts.MadeFaces(result, What(request));
    }

    /// <summary>The request as SOLIDWORKS takes it, every field checked: invalid_argument names the first that is wrong.</summary>
    static ExtrudeSpec Spec(FeatureExtrudeRequest r)
    {
        var e = r.End ?? ExtrudeEnd.Blind;
        bool depthEnd = e == ExtrudeEnd.Blind || e == ExtrudeEnd.MidPlane;
        bool upToEnd = e == ExtrudeEnd.UpToVertex || e == ExtrudeEnd.UpToSurface;
        string end = WireNames.ForEnum(e);
        if (depthEnd) FeatureFacts.Length(r.DepthMm, "depth_mm", $"a {end} extrude");
        else if (r.DepthMm != null) throw FeatureFacts.Invalid("depth_mm", $"depth_mm is for blind and mid_plane only, and this extrude ends {end}");
        if (upToEnd && r.UpTo == null) throw FeatureFacts.Invalid("up_to", $"an extrude that ends {end} needs up_to: the {(e == ExtrudeEnd.UpToVertex ? "vertex or point" : "face or plane")} it ends at");
        if (!upToEnd && r.UpTo != null) throw FeatureFacts.Invalid("up_to", $"up_to is for up_to_vertex and up_to_surface only, and this extrude ends {end}");
        if (e == ExtrudeEnd.MidPlane && r.Reverse == true) throw FeatureFacts.Invalid("reverse", "a mid_plane extrude goes both ways, so it cannot be reversed");
        if (r.DraftDeg != null && !(r.DraftDeg > 0 && r.DraftDeg < 90)) throw FeatureFacts.Invalid("draft_deg", "draft_deg must be more than 0 and less than 90 degrees");
        if (r.Mode == FeatureMode.Cut && r.Merge != null) throw FeatureFacts.Invalid("merge", "merge is for a boss; a cut always removes material from the bodies it meets");
        return new ExtrudeSpec
        {
            Cut = r.Mode == FeatureMode.Cut,
            End = e,
            DepthMm = r.DepthMm ?? 0,
            Reverse = r.Reverse == true,
            DraftDeg = r.DraftDeg ?? 0,
            DraftOutward = r.DraftOutward == true,
            Merge = r.Merge ?? true,
            Direction2 = r.Direction2 == null ? null : Second(r.Direction2, e),
            Thin = r.Thin == null ? null : Thin(r.Thin),
        };
    }

    /// <summary>The second direction, checked as the first is, its fields reported under /direction2.</summary>
    static ExtrudeSecond Second(ExtrudeDirection d, ExtrudeEnd first)
    {
        if (first == ExtrudeEnd.MidPlane) throw FeatureFacts.Invalid("direction2", "a mid_plane extrude already goes both ways, so it takes no direction2");
        var e = d.End ?? ExtrudeEnd.Blind;
        string end = WireNames.ForEnum(e);
        if (e == ExtrudeEnd.MidPlane) throw FeatureFacts.Invalid("/direction2/end", "a second direction cannot end mid_plane");
        bool upToEnd = e == ExtrudeEnd.UpToVertex || e == ExtrudeEnd.UpToSurface;
        if (e == ExtrudeEnd.Blind) FeatureFacts.Length(d.DepthMm, "/direction2/depth_mm", "a blind second direction");
        else if (d.DepthMm != null) throw FeatureFacts.Invalid("/direction2/depth_mm", $"depth_mm is for a blind second direction, and this one ends {end}");
        if (upToEnd && d.UpTo == null) throw FeatureFacts.Invalid("/direction2/up_to", $"a second direction that ends {end} needs up_to");
        if (!upToEnd && d.UpTo != null) throw FeatureFacts.Invalid("/direction2/up_to", $"up_to is for up_to_vertex and up_to_surface only, and this second direction ends {end}");
        if (d.DraftDeg != null && !(d.DraftDeg > 0 && d.DraftDeg < 90)) throw FeatureFacts.Invalid("/direction2/draft_deg", "draft_deg must be more than 0 and less than 90 degrees");
        return new ExtrudeSecond { End = e, DepthMm = d.DepthMm ?? 0, DraftDeg = d.DraftDeg ?? 0 };
    }

    /// <summary>The thin wall, its fields reported under /thin.</summary>
    static ThinSpec Thin(ExtrudeThin t)
    {
        var side = t.Side ?? ThinSide.OneSide;
        var spec = new ThinSpec { Side = side, ThicknessMm = FeatureFacts.Length(t.ThicknessMm, "/thin/thickness_mm", "a thin wall") };
        if (side == ThinSide.TwoSides) spec.Thickness2Mm = FeatureFacts.Length(t.Thickness2Mm, "/thin/thickness2_mm", "a thin wall on two sides");
        else if (t.Thickness2Mm != null) throw FeatureFacts.Invalid("/thin/thickness2_mm", "thickness2_mm is for a two_sides thin wall only");
        return spec;
    }
}

/// <summary>What every feature operation shares: request checks, the feature recorded in the transaction, the result filled, and the common checks.</summary>
public static class FeatureFacts
{
    public static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });

    /// <summary>A length that must be given, more than 0 and at most <see cref="SketchDraft.Reach"/> mm.</summary>
    public static double Length(double? value, string field, string what)
    {
        if (value == null) throw Invalid(field, $"{what} needs {field}");
        double v = value.Value;
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0 || v > SketchDraft.Reach)
            throw Invalid(field, $"{field} must be more than 0 and at most {SketchDraft.Reach} mm, not {v}");
        return v;
    }

    /// <summary>The sketch's contents, which must hold something that is not construction geometry.</summary>
    public static SketchContents Profile(ISwDocument doc, object sketch, string field, string what)
    {
        var contents = doc.ReadSketch(sketch);
        if (!contents.Segments.Any(s => !s.Construction))
            throw Invalid(field, $"{field}: {contents.Name} has nothing a {what} can use; draw a closed profile in it (sketch.create)");
        return contents;
    }

    /// <summary>The region the sketch's lines and circles enclose, or null when the profile has arcs, splines or open chains.</summary>
    public static ProfileRegion? Region(SketchContents c)
    {
        var drawn = c.Segments.Where(s => !s.Construction).ToList();
        if (drawn.Any(s => s.Kind != SketchSegmentKind.Line && s.Kind != SketchSegmentKind.Circle)) return null;
        return Profiles.Region(
            drawn.Where(s => s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null).Select(s => (s.Start!, s.End!)).ToList(),
            drawn.Where(s => s.Kind == SketchSegmentKind.Circle && s.Center != null && s.RadiusMm != null).Select(s => (s.Center!, s.RadiusMm!.Value)).ToList());
    }

    /// <summary>operation_failed for a feature SOLIDWORKS did not make, with its rebuild errors when it gave any.</summary>
    public static SwAgentException NotMade(ISwDocument doc, string what, string from, string hint)
    {
        var errors = doc.WhatsWrong().Where(i => !i.Warning).Select(i => $"{i.Feature}: {i.Code}").ToList();
        return new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS made no {what} {from}{(errors.Count > 0 ? $" ({string.Join(", ", errors)})" : "")}; {hint}. Nothing was changed",
            new JsonObject { ["errors"] = errors.Select(e => (object?)e).ToList() });
    }

    /// <summary>Records a feature just made in the transaction (deleting it, never its sketch, takes it back), and fills the result.</summary>
    public static FeatureResult Made(ReferenceService references, ISwDocument doc, object feature, ModelTransaction tx)
    {
        var made = new CreatedItem { Name = doc.Describe(feature).Name ?? "a feature", Kind = doc.KindOf(feature) ?? ReferenceKind.Feature };
        tx.Made(made, () =>
        {
            doc.Delete(feature, children: false, absorbed: false);
            doc.Rebuild(false);
        });
        var result = new FeatureResult
        {
            Feature = references.Describe(doc, feature),
            Faces = doc.FacesOf(feature).Select(f => references.Describe(doc, f)).ToList(),
        };
        made.Reference = result.Feature.Reference;
        return result;
    }

    /// <summary>The feature is in the tree with SOLIDWORKS' type name <paramref name="type"/> (IFeature.GetTypeName2).</summary>
    public static void ExpectType(ISwDocument doc, FeatureResult result, string type, string what)
    {
        string name = result.Feature.Name ?? "";
        var record = doc.Features().FirstOrDefault(f => f.Name == name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} {name} is not in the feature tree after it was made", new JsonObject { ["check"] = "feature_exists" });
        if (record.Type != type)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{name} is a {record.Type}, not the {type} a {what} makes",
                new JsonObject { ["check"] = "feature_type", ["expected"] = type, ["measured"] = record.Type });
        }
    }

    /// <summary>The volume moved the way the feature implies (<see cref="FeatureChecks.VolumeChanged"/>), recorded in the result's checks.</summary>
    public static void VolumeChanged(FeatureResult result, string what, double before, double after, int sign)
    {
        FeatureChecks.VolumeChanged(what, before, after, sign);
        string way = sign > 0 ? "grew" : sign < 0 ? "shrank" : "stayed";
        result.Checks.Add(FormattableString.Invariant($"the volume {way}: {before:0.###} -> {after:0.###} mm³"));
    }

    /// <summary>The feature made at least one face.</summary>
    public static void MadeFaces(FeatureResult result, string what)
    {
        if (result.Faces.Count == 0)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} {result.Feature.Name} made no faces", new JsonObject { ["check"] = "faces" });
    }
}
