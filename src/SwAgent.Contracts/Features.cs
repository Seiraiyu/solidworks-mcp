using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

/// <summary>Whether a feature adds material or removes it.</summary>
public enum FeatureMode
{
    Boss,
    Cut,
}

/// <summary>Where an extrude ends.</summary>
public enum ExtrudeEnd
{
    Blind,
    ThroughAll,
    UpToNext,
    UpToVertex,
    UpToSurface,
    MidPlane,
}

/// <summary>Which side of the profile's outline a thin wall is built on.</summary>
public enum ThinSide
{
    OneSide,
    OtherSide,
    MidPlane,
    TwoSides,
}

[Description("An extrude's second direction: the other way from the sketch plane, with its own end.")]
public sealed class ExtrudeDirection
{
    [Description("blind (depth_mm), through_all, up_to_next, up_to_vertex or up_to_surface (both name up_to). Default blind. Not mid_plane.")]
    public ExtrudeEnd? End { get; set; }

    [Description("How far, mm, for blind; more than 0. Not given for the other ends.")]
    public double? DepthMm { get; set; }

    [Description("For up_to_vertex, a vertex or point; for up_to_surface, a face or plane: its reference or name.")]
    public string? UpTo { get; set; }

    [Description("A draft angle for this direction, degrees, more than 0 and less than 90; no draft when absent.")]
    public double? DraftDeg { get; set; }
}

[Description("A thin-walled extrude: the profile's outline made into a wall of a thickness, instead of the profile filled.")]
public sealed class ExtrudeThin
{
    [Description("The wall's thickness, mm, more than 0.")]
    public double? ThicknessMm { get; set; }

    [Description("one_side (the default), other_side, mid_plane (half each side of the outline) or two_sides (thickness_mm on one side, thickness2_mm on the other).")]
    public ThinSide? Side { get; set; }

    [Description("For two_sides: the thickness on the other side, mm, more than 0.")]
    public double? Thickness2Mm { get; set; }
}

[Operation("feature.extrude", typeof(FeatureResult))]
[Description("Extrudes a sketch's closed profile along the sketch's normal, optionally in a second direction too and as a thin wall: a boss adds material, a cut removes it. Lengths in mm, angles in degrees. Checked: the feature exists with its type, rebuilds without error, and the part's volume grew (boss) or shrank (cut); for a blind or mid-plane boss (blind both ways with direction2) without draft or thin wall on a part with no body yet, the volume is exactly the profile's area times the total depth. Otherwise the feature is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureExtrudeRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch to extrude: its name (Sketch3) or its reference. Its closed loops are the profile.")]
    public string Sketch { get; set; } = "";

    [Description("boss (adds material) or cut (removes it). Default boss.")]
    public FeatureMode? Mode { get; set; }

    [Description("blind (depth_mm along the normal), mid_plane (depth_mm in total, half each side), through_all, up_to_next, up_to_vertex or up_to_surface (both name up_to). Default blind.")]
    public ExtrudeEnd? End { get; set; }

    [Description("How far, mm, for blind and mid_plane; more than 0. Not given for the other ends.")]
    public double? DepthMm { get; set; }

    [Description("For up_to_vertex, a vertex or point; for up_to_surface, a face or plane: its reference or name.")]
    public string? UpTo { get; set; }

    [Description("Extrude the other way, against the sketch's normal. Not with mid_plane. Default false.")]
    public bool? Reverse { get; set; }

    [Description("A draft angle, degrees, more than 0 and less than 90; no draft when absent.")]
    public double? DraftDeg { get; set; }

    [Description("The draft widens the profile as it goes instead of narrowing it. Default false.")]
    public bool? DraftOutward { get; set; }

    [Description("For a boss: merge it with the bodies it touches. Default true. Not for a cut.")]
    public bool? Merge { get; set; }

    [Description("Extrude the other way from the sketch plane as well, with its own end. Not with mid_plane.")]
    public ExtrudeDirection? Direction2 { get; set; }

    [Description("Make a thin wall from the profile's outline instead of filling it.")]
    public ExtrudeThin? Thin { get; set; }
}

[Operation("feature.revolve", typeof(FeatureResult))]
[Description("Revolves a sketch's closed profile about an axis: a boss adds material, a cut removes it. Angles in degrees. The axis is a line or centerline in the sketch, a reference axis or a straight edge. Checked: the feature exists with its type, rebuilds without error, and the part's volume grew (boss) or shrank (cut); for a boss on a part with no body yet, about a line in its own sketch, the volume is exactly Pappus' (angle × the profile's area × its centroid's distance from the axis). Otherwise the feature is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureRevolveRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch to revolve: its name or reference. Its closed loops are the profile, all on one side of the axis.")]
    public string Sketch { get; set; } = "";

    [Required]
    [Description("The axis: the reference of a line or centerline in the sketch (sketch.create gave it), or a reference axis's name or reference, or a straight edge's reference.")]
    public string Axis { get; set; } = "";

    [Description("boss (adds material) or cut (removes it). Default boss.")]
    public FeatureMode? Mode { get; set; }

    [Description("How far it turns, degrees: more than 0 and at most 360. Default 360.")]
    public double? AngleDeg { get; set; }

    [Description("Turn the other way. Default false.")]
    public bool? Reverse { get; set; }

    [Description("For a boss: merge it with the bodies it touches. Default true. Not for a cut.")]
    public bool? Merge { get; set; }
}
[Description("A feature made, with references to it and the faces it made, and the part after it.")]
public sealed class FeatureResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: what was made, and the part's rebuild status, bodies and volume before and after.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The feature: its name (Boss-Extrude2), reference and kind.")]
    public EntityInfo Feature { get; set; } = new EntityInfo();

    [Required]
    [Description("The faces the feature made, each with its reference and geometry; empty for reference geometry.")]
    public List<EntityInfo> Faces { get; set; } = new List<EntityInfo>();

    [Required]
    [Description("The exact checks made beyond the common ones, each as a sentence with what was expected and measured.")]
    public List<string> Checks { get; set; } = new List<string>();
}
