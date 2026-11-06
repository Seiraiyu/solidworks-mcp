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

[Operation("feature.loft", typeof(FeatureResult))]
[Description("Lofts between two or more sketch profiles in order, optionally following guide curves: a boss adds material, a cut removes it. Checked: the feature exists as a loft, rebuilds without error, the part has a solid body, and its volume grew (boss) or shrank (cut). Otherwise the loft is deleted and the part is as it was (rolled back); the sketches stay. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureLoftRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The profiles, in the order the loft passes through them: at least two sketches, by name or reference, each a closed profile on its own plane.")]
    public List<string> Profiles { get; set; } = new List<string>();

    [Description("Guide curves the loft follows between its profiles: sketches, by name or reference, each touching every profile.")]
    public List<string>? Guides { get; set; }

    [Description("boss (adds material) or cut (removes it). Default boss.")]
    public FeatureMode? Mode { get; set; }

    [Description("For a boss: merge it with the bodies it touches. Default true. Not for a cut.")]
    public bool? Merge { get; set; }
}

/// <summary>Which side of its sketch a rib is thickened to.</summary>
public enum RibSide
{
    Both,
    OneSide,
    OtherSide,
}

[Operation("feature.rib", typeof(FeatureResult))]
[Description("Makes a rib from an open sketch on a part that has a body: the sketch's lines are thickened into a wall that runs out until it meets the body. Lengths in mm, angles in degrees. Checked: the feature exists as a rib, rebuilds without error, the part's volume grew, and it made faces. Otherwise the rib is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureRibRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The rib's sketch: its name or reference. Usually one open line or chain, drawn on a plane through the body, its ends short of the body's walls.")]
    public string Sketch { get; set; } = "";

    [Description("The rib's thickness, mm, more than 0.")]
    public double? ThicknessMm { get; set; }

    [Description("both (the default: half the thickness each side of the sketch), one_side or other_side.")]
    public RibSide? Side { get; set; }

    [Description("Run the rib out normal to the sketch's plane instead of in it (parallel to the sketch, the default).")]
    public bool? NormalToSketch { get; set; }

    [Description("Run the material the other way from the sketch, when SOLIDWORKS' choice of side does not reach the body. Default false.")]
    public bool? FlipMaterial { get; set; }

    [Description("A draft angle, degrees, more than 0 and less than 90; no draft when absent.")]
    public double? DraftDeg { get; set; }

    [Description("The draft widens the rib as it goes instead of narrowing it. Default false. Only with draft_deg.")]
    public bool? DraftOutward { get; set; }
}
