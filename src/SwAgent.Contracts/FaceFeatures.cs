using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("feature.shell", typeof(FeatureResult))]
[Description("Hollows the part's body into walls of one thickness, leaving open the faces named (none: a closed hollow body). Thickness in mm. Checked: the feature exists as a shell, rebuilds without error, every face named is gone (it no longer resolves), and the volume shrank (inward, the default) or grew (outward). Otherwise the shell is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureShellRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The faces to remove, left open: their references (model.find kind face). None for a closed hollow body.")]
    public List<string>? FacesToRemove { get; set; }

    [Description("The walls' thickness, mm, more than 0.")]
    public double? ThicknessMm { get; set; }

    [Description("Build the walls outside the body instead of inside it. Default false.")]
    public bool? Outward { get; set; }
}

[Operation("feature.draft", typeof(FeatureResult))]
[Description("Tapers faces by an angle about a neutral plane (a plane or planar face that keeps its size), as a mould needs. Angles in degrees. Checked: the feature exists as a draft, rebuilds without error, the volume changed, and every planar face it drafted now stands at 90 degrees less the draft angle to the neutral plane's normal. Otherwise the draft is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureDraftRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The neutral plane: a plane's name (front, top, right, Plane1) or reference, or a planar face's reference. The draft pulls away from it along its normal.")]
    public string NeutralPlane { get; set; } = "";

    [Required]
    [Description("The faces to taper: their references (model.find kind face), each meeting the neutral plane at an angle.")]
    public List<string> Faces { get; set; } = new List<string>();

    [Description("The draft angle, degrees, more than 0 and less than 90.")]
    public double? AngleDeg { get; set; }

    [Description("Pull the other way, against the neutral plane's normal. Default false.")]
    public bool? Reverse { get; set; }
}
