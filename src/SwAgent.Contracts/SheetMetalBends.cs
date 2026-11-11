using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("sheetmetal.sketched_bend", typeof(SheetMetalFeatureResult))]
[Description("Bends a sheet-metal part along lines the agent drew on one of its planar faces (sketch.create on that face): each line is one bend, so two lines make a U channel in one call. The side of the lines that holds the fixed face's centre stays where it is; the rest turns angle_deg about each line. Folded parts only: a flattened part is refused. Lengths in mm, angles in degrees. Checked before SOLIDWORKS is called: the sketch is lines only, on the fixed face's plane, and the fixed face's centre is on the face and off every line. Checked after: one new bend per line at the angle, inner radius and allowance asked; the volume changed by exactly the bend arithmetic's folded material less what each bend develops to (nothing at K 0.5), wherever a bend's width can be measured; a face of the sheet still lies in the fixed face's plane through its centre, facing the same way; the thickness is unchanged. Otherwise the bend is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalSketchedBendRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch: its name (Sketch2) or reference. Lines only (construction lines are ignored), on a planar face of the sheet; each line is one bend and may run past the sheet's edges.")]
    public string Sketch { get; set; } = "";

    [Required]
    [Description("The planar face of the sheet the sketch is on, by reference (sheetmetal.read gives the base face). The side of the lines holding its centre stays put.")]
    public string FixedFace { get; set; } = "";

    [Description("How far each bend turns, degrees, more than 0 and less than 180.")]
    public double AngleDeg { get; set; }

    [Description("The bends' inner radius, mm, more than 0. The part's default when absent.")]
    public double? BendRadiusMm { get; set; }

    [Description("Where each bend sits relative to its line: bend_outside (the default: the bend starts at the line, on the side that turns), material_inside or material_outside.")]
    public BendPosition? Position { get; set; }

    [Description("Bend to the other side of the sheet. Default false.")]
    public bool? Flip { get; set; }

    [Description("The bends' own allowance: exactly one of k_factor, allowance_mm or deduction_mm. The part's default when absent.")]
    public BendAllowance? Allowance { get; set; }
}
