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

/// <summary>The Hole Wizard's hole types this stage offers.</summary>
public enum HoleType
{
    Simple,
    Counterbore,
    Countersink,
    Tapped,
}

/// <summary>The hole standards this stage offers: the metric ones.</summary>
public enum HoleStandard
{
    Iso,
    AnsiMetric,
}

/// <summary>Where a hole ends.</summary>
public enum HoleEnd
{
    Blind,
    ThroughAll,
}

[Operation("feature.hole", typeof(FeatureResult))]
[Description("Drills Hole Wizard holes into a planar face at the positions given: simple (drill sizes), counterbore (for socket head cap screws), countersink (for countersunk flat head screws) or tapped, to an ISO or ANSI metric standard, blind or through all. The add-in first sketches a point at each position on the face. Lengths in mm. Checked: the feature exists as a Hole Wizard hole, rebuilds without error, the volume shrank, and the hole made one cylinder (or set of coaxial cylinders) per position, each on its position's axis. Otherwise the hole and its sketch are deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class FeatureHoleRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The planar face the holes start on: its reference (model.find kind face).")]
    public string Face { get; set; } = "";

    [Required]
    [Description("Where each hole goes: points on the face, in the part's model space, mm.")]
    public List<Point3> Positions { get; set; } = new List<Point3>();

    [Description("simple (the default), counterbore, countersink or tapped.")]
    public HoleType? Type { get; set; }

    [Description("iso (the default) or ansi_metric.")]
    public HoleStandard? Standard { get; set; }

    [Required]
    [Description("The size exactly as SOLIDWORKS' Hole Wizard lists it for the type and standard: M5 for a counterbore, countersink or tapped hole; Ø5.0 for an ISO simple hole (a drill size).")]
    public string Size { get; set; } = "";

    [Description("blind (the default: depth_mm deep) or through_all.")]
    public HoleEnd? End { get; set; }

    [Description("How deep, mm, for blind; more than 0. Not given for through_all.")]
    public double? DepthMm { get; set; }
}
