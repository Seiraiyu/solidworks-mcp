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

[Operation("sheetmetal.unfold", typeof(SheetMetalFeatureResult))]
[Description("Unfolds chosen bends of a sheet-metal part with an Unfold feature, keeping fixed_face where it is, so a cut can be made across them (feature.extrude); sheetmetal.fold folds them back with the cut in them. bends names the bends (sheetmetal.read lists them, by name or reference); every folded bend when absent. A bend already unfolded is refused. Folded parts only: a flattened part is refused. Checked: the feature is an Unfold; each bend named has no cylindrical face left; a face of the sheet still lies in the fixed face's plane through its centre; the volume changed by exactly the bend arithmetic's sum over those bends where each is whole, and by no more than that where one is cut across. Otherwise it is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalUnfoldRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("A planar face of the sheet, by reference, that stays where it is (sheetmetal.read gives the base face).")]
    public string FixedFace { get; set; } = "";

    [Description("The bends to unfold, each by name (EdgeBend1) or reference. Every folded bend when absent.")]
    public List<string>? Bends { get; set; }
}

[Operation("sheetmetal.fold", typeof(SheetMetalFeatureResult))]
[Description("Folds back bends a sheet-metal part's Unfold features unfolded, with a Fold feature, keeping the face fixed that the unfold kept (or fixed_face): what was cut across them while unfolded is folded with them. bends names the bends; every unfolded bend when absent. A bend that is not unfolded is refused. Folded parts only (the Flat-Pattern feature off): a flattened part is refused. Checked: the feature is a Fold; each bend named has its cylindrical faces back; the fixed face is where it was; the volume changed by exactly the bend arithmetic's sum over those bends, the other way, where each is whole again, and by no more than that where one was cut across (so at K 0.5 the part is its folded volume less what was cut). Otherwise it is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalFoldRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The bends to fold, each by name (EdgeBend1) or reference. Every unfolded bend when absent.")]
    public List<string>? Bends { get; set; }

    [Description("A planar face of the sheet, by reference, that stays where it is. The fixed face of the Unfold that unfolded the bends when absent.")]
    public string? FixedFace { get; set; }
}

[Operation("sheetmetal.convert", typeof(SheetMetalFeatureResult))]
[Description("Turns an ordinary solid of constant wall thickness (an L or U profile extruded) into the part's sheet metal: fixed_face stays where it is, and each sharp inner edge named becomes a bend of bend_radius_mm (inner radius). The allowance becomes the part's default (SOLIDWORKS' own when absent). Refused on a part that is already sheet metal: a part has one sheet-metal body. Lengths in mm. Checked before SOLIDWORKS is called: the fixed face is planar and borders a bend edge; each bend edge is straight, between two planar faces, and an inner (concave) corner. Checked after: the part is sheet metal with one sheet body, Sheet-Metal and Flat-Pattern exist, the thickness reads back and measures thickness_mm; one bend per edge named at the radius asked; the part's box is unchanged within the thickness. Otherwise everything it made is deleted and the part is as it was (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalConvertRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The face that stays put, by reference: a planar inner face of the solid, one that borders a bend edge (model.find gives it). An outer face is refused.")]
    public string FixedFace { get; set; } = "";

    [Required]
    [Description("The sharp inner edges that become bends, by reference (model.find gives them).")]
    public List<string> BendEdges { get; set; } = new List<string>();

    [Description("The sheet's thickness, mm, more than 0: the solid's wall thickness.")]
    public double ThicknessMm { get; set; }

    [Description("The bends' inner radius, mm, more than 0; it becomes the part's default.")]
    public double BendRadiusMm { get; set; }

    [Description("The part's default bend allowance: exactly one of k_factor, allowance_mm or deduction_mm. SOLIDWORKS' own default when absent.")]
    public BendAllowance? Allowance { get; set; }

    [Description("Thicken to the other side of the fixed face (inward, so the part shrinks by the thickness). Default false.")]
    public bool? ReverseThickness { get; set; }

    [Description("Keep the original solid as a body beside the sheet. Default false.")]
    public bool? KeepBody { get; set; }
}
