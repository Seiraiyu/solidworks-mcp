using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A linear pattern's second direction: its own direction, spacing and count.")]
public sealed class PatternDirection
{
    [Required]
    [Description("The direction: a straight edge, a reference axis, or a plane or planar face (along its normal): its name or reference.")]
    public string Direction { get; set; } = "";

    [Description("The distance between instances, mm, more than 0.")]
    public double? SpacingMm { get; set; }

    [Description("How many instances in this direction, the seed's row included: 2 to 500.")]
    public int? Count { get; set; }

    [Description("Go the other way along the direction. Default false.")]
    public bool? Reverse { get; set; }
}

[Operation("feature.linear_pattern", typeof(FeatureResult))]
[Description("Repeats features, or faces, in rows along a direction, and optionally in columns along a second one. Lengths in mm. Faces are repeated as SOLIDWORKS' geometry pattern and checked by the instances and that the volume moved. For features, the seeds' own volume is measured first by suppressing them for a moment and putting them back, unless another feature is built on them. Checked: the feature exists as a linear pattern with the instances asked, rebuilds without error, and the volume changed by the seeds' volume once for each new instance (or, when it could not be measured, changed at all); an instance that runs off the part or into another changes it by a different amount and is refused. Otherwise the pattern is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureLinearPatternRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The features to repeat (the seeds): their names (Cut-Extrude1) or references. Give features or faces.")]
    public List<string>? Features { get; set; }

    [Description("Faces to repeat instead of features (a face pattern: SOLIDWORKS' geometry pattern): their references (model.find kind face).")]
    public List<string>? Faces { get; set; }

    [Required]
    [Description("The first direction: a straight edge, a reference axis, or a plane or planar face (along its normal): its name or reference.")]
    public string Direction { get; set; } = "";

    [Description("The distance between instances, mm, more than 0.")]
    public double? SpacingMm { get; set; }

    [Description("How many instances along the direction, the seed included: 2 to 500.")]
    public int? Count { get; set; }

    [Description("Go the other way along the direction. Default false.")]
    public bool? Reverse { get; set; }

    [Description("A second direction, making a grid.")]
    public PatternDirection? Direction2 { get; set; }
}
