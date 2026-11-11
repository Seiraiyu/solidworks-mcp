using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

/// <summary>
/// Where a flange's bend sits relative to the edge it is made on (SOLIDWORKS' flange position). bend_centerline is not
/// offered: CADapter measured it a silent no-op on the sketched bend, and the flanges have not been proven with it.
/// </summary>
public enum BendPosition
{
    /// <summary>The flange's outside face stays within the edge: the sheet's outline does not grow.</summary>
    MaterialInside,

    /// <summary>The flange's inside face stays on the edge: the flange's thickness is outside it.</summary>
    MaterialOutside,

    /// <summary>The bend starts at the edge: the bend and the flange are outside it.</summary>
    BendOutside,
}

/// <summary>Where an edge flange's length is measured from.</summary>
public enum FlangeLengthFrom
{
    /// <summary>From where the outside faces of the sheet and the flange would meet without the bend.</summary>
    OuterVirtualSharp,

    /// <summary>From where the inside faces of the sheet and the flange would meet without the bend.</summary>
    InnerVirtualSharp,

    /// <summary>From where the bend ends: the flange's straight part only.</summary>
    Tangent,
}

/// <summary>The shape of a hem: the edge folded back on itself.</summary>
public enum HemKind
{
    /// <summary>Folded flat onto the sheet, length_mm long.</summary>
    Closed,

    /// <summary>Folded back with gap_mm between it and the sheet, length_mm long.</summary>
    Open,

    /// <summary>Rolled through angle_deg at radius_mm, closing back towards the sheet like a teardrop.</summary>
    Teardrop,

    /// <summary>Rolled through angle_deg at radius_mm, open.</summary>
    Rolled,
}

[Operation("sheetmetal.edge_flange", typeof(SheetMetalFeatureResult))]
[Description("Bends a flange up from straight edges of a sheet-metal part, one flange per edge: each rises from the edge towards the outward normal of the sheet's face the edge bounds (to bend the other way, take the edge of the opposite face), turned angle_deg from the sheet and length_mm long. The add-in draws each flange's profile itself; the agent gives only the edges (sheetmetal.read gives the base face, model.find its edges). Folded parts only: a flattened part is refused. Lengths in mm, angles in degrees. Checked for each flange: one new bend at the angle, inner radius and allowance asked; the volume grows by exactly the flange's straight part and bend less the sheet its bend takes back (within the reliefs SOLIDWORKS cuts when offset_mm leaves sheet beside it); up to 90°, the part reaches the flange's height above the face. Otherwise every flange made is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalEdgeFlangeRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The edges, by reference: straight edges where the sheet ends, each between one of its faces and the thin face it ends in. Each gets its own flange, in order.")]
    public List<string> Edges { get; set; } = new List<string>();

    [Description("How long each flange is, mm, more than 0, measured from length_from.")]
    public double LengthMm { get; set; }

    [Description("How far each flange turns from the sheet, degrees, more than 0 and less than 180. Default 90.")]
    public double? AngleDeg { get; set; }

    [Description("Where length_mm is measured from: outer_virtual_sharp (the default), inner_virtual_sharp or tangent (the straight part only).")]
    public FlangeLengthFrom? LengthFrom { get; set; }

    [Description("Where the bend sits relative to the edge: material_inside (the default: the flange's outside face on the edge, so the outline does not grow), material_outside or bend_outside.")]
    public BendPosition? Position { get; set; }

    [Description("The bends' inner radius, mm, more than 0. The part's default when absent.")]
    public double? BendRadiusMm { get; set; }

    [Description("The bends' own allowance: exactly one of k_factor, allowance_mm or deduction_mm. The part's default when absent.")]
    public BendAllowance? Allowance { get; set; }

    [Description("How far each flange stops short of its edge's two ends, mm, 0 or more, less than half the edge. Default 0: the whole edge.")]
    public double? OffsetMm { get; set; }
}

[Operation("sheetmetal.miter_flange", typeof(SheetMetalFeatureResult))]
[Description("Runs a flange along edges of a sheet-metal part with a profile the agent drew: an open chain of lines in a sketch on a plane square to the first edge, touching one of that edge's ends (refgeom.plane with normal_to_curve makes such a plane at the end; sketch.create draws in it). Each line becomes a straight part of the flange and each corner a bend, with one more bend where the flange meets the sheet; several edges must meet end to end, and the flange turns their corners leaving gap_mm. Folded parts only: a flattened part is refused. Lengths in mm. Checked before SOLIDWORKS is called: the profile is one open chain of lines, its plane is square to the first edge, and it touches that edge's end (SOLIDWORKS refuses silently 2 mm away). Checked after: the feature is a miter flange with a bend per profile corner and one where it meets the sheet, for every edge, at the radius and allowance asked; the volume grew, by no more than the profile and its bends along the edges; a profile of one line square to the sheet leaves the part as long as the line that way (plus at most the thickness). Otherwise it is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalMiterFlangeRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The profile's sketch: its name (Sketch2) or reference. One open chain of lines, on a plane square to the first edge, touching one of its ends.")]
    public string Sketch { get; set; } = "";

    [Required]
    [Description("The edges, by reference, in order along the flange: straight edges where the sheet ends, meeting end to end. The first is the one the profile touches.")]
    public List<string> Edges { get; set; } = new List<string>();

    [Description("The gap the flange leaves where it turns a corner between two edges, mm, 0 or more. Default 0.5.")]
    public double? GapMm { get; set; }

    [Description("Where the bend sits relative to the edges: material_inside (the default), material_outside or bend_outside.")]
    public BendPosition? Position { get; set; }

    [Description("The bends' inner radius, mm, more than 0. The part's default when absent.")]
    public double? BendRadiusMm { get; set; }

    [Description("The bends' own allowance: exactly one of k_factor, allowance_mm or deduction_mm. The part's default when absent.")]
    public BendAllowance? Allowance { get; set; }
}
