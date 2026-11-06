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
