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
