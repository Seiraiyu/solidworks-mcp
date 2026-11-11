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

[Operation("sheetmetal.flatten", typeof(SheetMetalFlattenResult))]
[Description("Flattens a sheet-metal part (flat true) or folds it back (flat false), by turning its Flat-Pattern feature on or off; a part already in that state is left as it is. The state persists until it is changed again, and sheetmetal.read reports it; the flange, hem, bend, convert and parameter tools need the part folded. Checked: the state reads back from the Flat-Pattern feature; the volume changed by exactly the bend arithmetic's sum over the bends, each developing to its allowance (nothing at K 0.5), where every bend is whole, and by no more than that sum where one is cut across; flattened, no bend has a cylindrical face left and the sheet is its thickness thin across its base face. Otherwise the Flat-Pattern feature is put back as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open.")]
public sealed class SheetMetalFlattenRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("True to flatten the part, false to fold it back.")]
    public bool Flat { get; set; }
}

[Description("A sheet-metal part flattened or folded, its flat size, and the part after it.")]
public sealed class SheetMetalFlattenResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: the Flat-Pattern feature's suppression before and after, and the part's rebuild status, bodies and volume before and after.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Description("folded or flattened, as the Flat-Pattern feature reads now.")]
    public SheetState State { get; set; }

    [Description("True when the part was already in the state asked, so nothing was changed.")]
    public bool Unchanged { get; set; }

    [Description("The flat pattern's size from the cut list; absent when the cut list does not give it as numbers.")]
    public FlatSize? Flat { get; set; }

    [Required]
    [Description("The checks made, each as a sentence with what was expected and measured.")]
    public List<string> Checks { get; set; } = new List<string>();
}
