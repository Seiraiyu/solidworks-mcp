using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class FeatureShellOperation : ModelChangeOperation<FeatureShellRequest, FeatureResult>
{
    public FeatureShellOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureShellRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, FeatureShellRequest request, ModelTransaction tx, IOperationContext context)
    {
        double thickness = FeatureFacts.Length(request.ThicknessMm, "thickness_mm", "a shell");
        if (BodiesBefore == 0) throw FeatureFacts.Invalid("faces_to_remove", "a shell hollows a body, and this part has none yet");
        var faces = FeatureFacts.Entities(Services.References, doc, request.FacesToRemove, "faces_to_remove", ReferenceKind.Face);
        var feature = doc.Shell(faces, thickness, request.Outward == true)
            ?? throw FeatureFacts.NotMade(doc, "shell", FormattableString.Invariant($"of {thickness:0.###} mm"), "check that the walls fit: a wall thicker than the smallest radius of curvature inside the body cannot be made");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The feature is a shell, the faces named are gone, and the volume moved the way the walls go.</summary>
    protected override void Check(ISwDocument doc, FeatureShellRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Shell", "shell");
        FeatureFacts.Gone(Services.References, doc, request.FacesToRemove, "faces_to_remove", ReferenceKind.Face, result, "shell");
        FeatureFacts.VolumeChanged(result, "shell", VolumeBefore, Volume(doc, doc.SolidBodyCount()), request.Outward == true ? 1 : -1);
        FeatureFacts.MadeFaces(result, "shell");
    }
}

public sealed class FeatureDraftOperation : ModelChangeOperation<FeatureDraftRequest, FeatureResult>
{
    public FeatureDraftOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureDraftRequest request) => request.Document;

    /// <summary>How far a drafted face may stand from 90° less the draft to the neutral normal, degrees.</summary>
    public const double AngleToleranceDeg = 1e-4;

    protected override FeatureResult Change(ISwDocument doc, FeatureDraftRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.AngleDeg == null || !(request.AngleDeg > 0 && request.AngleDeg < 90)) throw FeatureFacts.Invalid("angle_deg", "angle_deg must be given, more than 0 and less than 90 degrees");
        if (request.Faces.Count == 0) throw FeatureFacts.Invalid("faces", "a draft needs the faces to taper");
        var references = Services.References;
        var (neutral, _) = references.Resolve(doc, request.NeutralPlane, "neutral_plane", ReferenceKind.Plane, ReferenceKind.Face);
        NeutralNormal(doc, neutral);
        var faces = FeatureFacts.Entities(references, doc, request.Faces, "faces", ReferenceKind.Face);
        int same = faces.FindIndex(f => ReferenceEquals(f, neutral));
        if (same >= 0) throw FeatureFacts.Invalid($"/faces/{same}", $"/faces/{same} is the neutral plane itself; it keeps its size and is not drafted");
        var spec = new DraftSpec { Neutral = neutral, AngleDeg = request.AngleDeg.Value, Reverse = request.Reverse == true };
        spec.Faces.AddRange(faces);
        var feature = doc.Draft(spec)
            ?? throw FeatureFacts.NotMade(doc, "draft", FormattableString.Invariant($"of {spec.AngleDeg:0.###}°"), "check that every face meets the neutral plane and that the angle leaves the faces' neighbours room");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    /// <summary>The neutral plane's normal; invalid_argument when it is a face that is not planar.</summary>
    static Vector3 NeutralNormal(ISwDocument doc, object neutral)
    {
        var info = doc.Describe(neutral);
        if (info.Geometry != GeometryType.Plane || info.Normal == null)
            throw FeatureFacts.Invalid("neutral_plane", $"neutral_plane: a draft's neutral plane is a plane or a planar face, and this is a {WireNames.ForEnum(info.Geometry)}");
        return info.Normal;
    }

    /// <summary>The feature is a draft, the volume changed, and every planar face it drafted stands at 90° less the draft to the neutral normal.</summary>
    protected override void Check(ISwDocument doc, FeatureDraftRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Draft", "draft");
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureChecks.VolumeMoved("draft", VolumeBefore, after);
        result.Checks.Add(FormattableString.Invariant($"the volume changed: {VolumeBefore:0.###} -> {after:0.###} mm³"));
        FeatureFacts.MadeFaces(result, "draft");
        var (neutral, _) = Services.References.Resolve(doc, request.NeutralPlane, "neutral_plane", ReferenceKind.Plane, ReferenceKind.Face);
        var normal = NeutralNormal(doc, neutral);
        double expected = 90 - request.AngleDeg!.Value;
        int measured = 0;
        for (int i = 0; i < request.Faces.Count; i++)
        {
            object face;
            try
            {
                face = Services.References.Resolve(doc, request.Faces[i], $"/faces/{i}", ReferenceKind.Face).Entity;
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved)
            {
                continue; // SOLIDWORKS made the drafted face anew; the draft's own faces are measured below
            }
            var info = doc.Describe(face);
            if (info.Geometry != GeometryType.Plane || info.Normal == null) continue;
            FeatureChecks.Near("draft_angle", $"/faces/{i}'s angle to the neutral plane's normal", expected, FeatureChecks.AngleDeg(info.Normal, normal), AngleToleranceDeg, "°");
            measured++;
        }
        if (measured == 0)
        {
            var planar = result.Faces.Where(f => f.Geometry == GeometryType.Plane && f.Normal != null).ToList();
            if (planar.Count > 0 && !planar.Any(f => Math.Abs(FeatureChecks.AngleDeg(f.Normal!, normal) - expected) <= AngleToleranceDeg))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    FormattableString.Invariant($"no face of {result.Feature.Name} stands at {expected:0.###}° to the neutral plane's normal"),
                    new JsonObject { ["check"] = "draft_angle", ["expected"] = expected, ["measured"] = planar.Select(f => (object?)FeatureChecks.AngleDeg(f.Normal!, normal)).ToList() });
            }
            measured = planar.Count;
        }
        result.Checks.Add(FormattableString.Invariant($"{measured} drafted planar faces stand at {expected:0.###}° to the neutral plane's normal"));
    }
}
