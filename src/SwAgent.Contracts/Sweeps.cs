using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("feature.sweep", typeof(FeatureResult))]
[Description("Sweeps a sketch's closed profile along a path, optionally following guide curves: a boss adds material, a cut removes it. The profile keeps its angle to the path as it goes. Checked: the feature exists as a sweep, rebuilds without error, the part has a solid body, and its volume grew (boss) or shrank (cut); for a boss along one straight line on a part with no body yet, the volume is exactly the profile's area times the line's length along the profile's normal. Otherwise the sweep is deleted and the part is as it was (rolled back); the sketches stay. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureSweepRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch to sweep: its name (Sketch2) or reference. Its closed loops are the profile.")]
    public string Profile { get; set; } = "";

    [Required]
    [Description("The path: a sketch (its name or reference) whose segments run end to end, or a model edge's reference. It meets the profile's plane where the profile is.")]
    public string Path { get; set; } = "";

    [Description("Guide curves the profile follows as it sweeps: sketches, by name or reference, each touching the profile.")]
    public List<string>? Guides { get; set; }

    [Description("boss (adds material) or cut (removes it). Default boss.")]
    public FeatureMode? Mode { get; set; }

    [Description("For a boss: merge it with the bodies it touches. Default true. Not for a cut.")]
    public bool? Merge { get; set; }
}
