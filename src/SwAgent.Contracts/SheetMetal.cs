using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("How a bend develops when the sheet is flattened. Give exactly one of k_factor, allowance_mm or deduction_mm.")]
public sealed class BendAllowance
{
    [Description("The K-factor: where the neutral layer sits across the thickness, more than 0 and at most 1 (0.5 is the middle). The bend develops to angle × (inner radius + K × thickness).")]
    public double? KFactor { get; set; }

    [Description("The bend allowance itself, mm, more than 0: the length the bend develops to, whatever its angle.")]
    public double? AllowanceMm { get; set; }

    [Description("The bend deduction, mm, 0 or more: what the bend takes off the sum of the two flanges' outer lengths to the virtual sharp.")]
    public double? DeductionMm { get; set; }

    public override string ToString() =>
        KFactor != null ? System.FormattableString.Invariant($"K {KFactor:0.####}")
        : AllowanceMm != null ? System.FormattableString.Invariant($"allowance {AllowanceMm:0.####} mm")
        : DeductionMm != null ? System.FormattableString.Invariant($"deduction {DeductionMm:0.####} mm")
        : "none";
}

/// <summary>The relief SOLIDWORKS cuts where a bend meets material that does not bend.</summary>
public enum ReliefType
{
    Rectangular,
    Obround,
    Tear,
}

/// <summary>Whether a sheet-metal part shows its formed shape or its flat pattern.</summary>
public enum SheetState
{
    Folded,
    Flattened,
}

[Description("A bend of a sheet-metal part, as SOLIDWORKS reads it back.")]
public sealed class BendInfo
{
    [Required]
    [Description("Its name, for example EdgeBend1 or BaseBend1.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference (a feature reference). References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Required]
    [Description("The feature it belongs to, for example Base-Flange1.")]
    public string Feature { get; set; } = "";

    [Description("How far it bends, degrees: 90 for a right angle.")]
    public double AngleDeg { get; set; }

    [Description("Its inner radius, mm.")]
    public double InnerRadiusMm { get; set; }

    [Required]
    [Description("How it develops when flattened; empty when SOLIDWORKS takes it from a bend or gauge table, which is not read.")]
    public BendAllowance Allowance { get; set; } = new BendAllowance();

    [Description("True when its radius is the part's default.")]
    public bool DefaultRadius { get; set; }

    [Description("True when its allowance is the part's default.")]
    public bool DefaultAllowance { get; set; }

    [Description("How long it is along its axis, mm; absent when its axis is not along x, y or z.")]
    public double? WidthMm { get; set; }
}

[Operation("sheetmetal.base_flange", typeof(SheetMetalFeatureResult))]
[Description("Starts a sheet-metal part from a sketch: a closed profile becomes a flat tab thickness_mm thick; an open chain is extruded depth_mm and each of its sharp corners becomes a bend of bend_radius_mm (inner radius). The allowance becomes the part's default (K-factor 0.5 when absent), and the relief its automatic relief. One sheet-metal body per part: a part that already has one is refused, because thickness belongs to the part and a second body would change the first. Lengths in mm, angles in degrees. Checked: the part is sheet metal with one sheet body, Sheet-Metal and Flat-Pattern exist, the thickness reads back and measures between the sheet's two faces, the radius and allowance read back; a closed profile adds exactly its area times the thickness, and an open chain of lines its straight legs plus each bend's material, with a bend at each corner. Otherwise everything it made is deleted and the part is as it was (rolled back); the sketch stays. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalBaseFlangeRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sketch: its name (Sketch1) or reference. A closed profile, or one open chain whose lines are one face of the sheet.")]
    public string Sketch { get; set; } = "";

    [Description("The sheet's thickness, mm, more than 0.")]
    public double ThicknessMm { get; set; }

    [Description("The default inner bend radius, mm, more than 0: the bends at an open chain's corners, and later bends that do not give their own.")]
    public double BendRadiusMm { get; set; }

    [Description("The part's default bend allowance: exactly one of k_factor, allowance_mm or deduction_mm. Default k_factor 0.5.")]
    public BendAllowance? Allowance { get; set; }

    [Description("Thicken to the other side of the sketch. Default false.")]
    public bool? Reverse { get; set; }

    [Description("For an open chain only: how far it is extruded, mm, more than 0, along the sketch's normal.")]
    public double? DepthMm { get; set; }

    [Description("The automatic relief where a bend meets material that does not bend: rectangular (the default), obround or tear.")]
    public ReliefType? Relief { get; set; }

    [Description("The relief's size as a ratio of the thickness, at least 0.05 and at most 2. Default 0.5.")]
    public double? ReliefRatio { get; set; }
}

[Description("A sheet-metal feature made, with references to it and the faces it made, the sheet-metal facts read back, and the part after it.")]
public sealed class SheetMetalFeatureResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: what was made, and the part's rebuild status, bodies and volume before and after.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The feature: its name (Base-Flange1), reference and kind.")]
    public EntityInfo Feature { get; set; } = new EntityInfo();

    [Description("Every feature made, in order, when the operation made more than one (an edge flange makes one per edge); absent when it made one, which is feature.")]
    public List<EntityInfo>? Features { get; set; }

    [Required]
    [Description("The faces the feature (or the features) made, each with its reference and geometry.")]
    public List<EntityInfo> Faces { get; set; } = new List<EntityInfo>();

    [Required]
    [Description("The sheet's thickness and state, and the bends the feature (or the features) made.")]
    public SheetMetalFacts Sheet { get; set; } = new SheetMetalFacts();

    [Required]
    [Description("The exact checks made beyond the common ones, each as a sentence with what was expected and measured.")]
    public List<string> Checks { get; set; } = new List<string>();
}

[Operation("sheetmetal.read", typeof(SheetMetalReadResult), ReadOnly = true)]
[Description("Reads a part as a sheet-metal designer does: whether it is sheet metal, whether it is folded or flattened, its thickness, default bend radius, allowance and relief, its sheet-metal body and any ordinary bodies beside it, every bend with its angle, inner radius, allowance and width, its base face (the sheet's largest planar face, whose edges usually take flanges), and the flat pattern's size from SOLIDWORKS' cut list with the cut list's other properties. An ordinary part answers sheet_metal false and nothing more. Lengths in mm, angles in degrees. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SheetMetalReadRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part read as sheet metal.")]
public sealed class SheetMetalReadResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("True when the part is sheet metal (it has a SheetMetal feature). When false, nothing below is given.")]
    public bool SheetMetal { get; set; }

    [Description("folded or flattened.")]
    public SheetState? State { get; set; }

    [Description("The sheet's thickness, mm.")]
    public double? ThicknessMm { get; set; }

    [Description("The default inner bend radius, mm.")]
    public double? BendRadiusMm { get; set; }

    [Description("The default bend allowance; empty when SOLIDWORKS takes it from a bend or gauge table, which is not read.")]
    public BendAllowance? Allowance { get; set; }

    [Description("The automatic relief; absent when there is none or it is not rectangular, obround or tear.")]
    public ReliefType? Relief { get; set; }

    [Description("The relief's size as a ratio of the thickness.")]
    public double? ReliefRatio { get; set; }

    [Description("How many solid bodies are sheet metal (one, in a part this server made).")]
    public int SheetBodies { get; set; }

    [Description("How many solid bodies are ordinary solids beside the sheet.")]
    public int OtherBodies { get; set; }

    [Required]
    [Description("Every bend, in tree order.")]
    public List<BendInfo> Bends { get; set; } = new List<BendInfo>();

    [Description("The sheet's largest planar face, with its reference; absent when its body has no planar face.")]
    public EntityInfo? BaseFace { get; set; }

    [Description("The flat pattern's size from the cut list; absent when the cut list does not give it as numbers.")]
    public FlatSize? Flat { get; set; }

    [Required]
    [Description("Every property of the sheet's cut-list item, as name = value, in SOLIDWORKS' own names and the part's units.")]
    public List<string> CutList { get; set; } = new List<string>();
}

[Operation("sheetmetal.set_parameters", typeof(SheetMetalSetParametersResult))]
[Description("Changes a sheet-metal part's thickness, default bend radius, default allowance or automatic relief; give at least one. The radius and allowance reach every bend that uses the part's default (SOLIDWORKS says it does, or it has the old default's value). Folded parts only: a flattened part is refused. Lengths in mm. Checked on the part, never on SOLIDWORKS' answer: every value reads back; the thickness measures between the sheet's two faces; every bend that follows the default reads back the new radius and allowance, and its cylindrical faces measure the new radius; an allowance-only change moves the flat size by exactly what the bend arithmetic predicts when the bends are square to each other, and leaves the folded volume as it was. Otherwise every value is put back (rolled back). Busy with user_editing while the user is editing a sketch or has a command open.")]
public sealed class SheetMetalSetParametersRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The new thickness, mm, more than 0.")]
    public double? ThicknessMm { get; set; }

    [Description("The new default inner bend radius, mm, more than 0.")]
    public double? BendRadiusMm { get; set; }

    [Description("The new default bend allowance: exactly one of k_factor, allowance_mm or deduction_mm.")]
    public BendAllowance? Allowance { get; set; }

    [Description("The new automatic relief: rectangular, obround or tear.")]
    public ReliefType? Relief { get; set; }

    [Description("The relief's new size as a ratio of the thickness, at least 0.05 and at most 2.")]
    public double? ReliefRatio { get; set; }
}

[Description("A sheet-metal part's parameters changed, every bend read back, and the part after it.")]
public sealed class SheetMetalSetParametersResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: each value changed with its before and after, and the part's rebuild status, bodies and volume before and after.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The sheet's thickness and state, and every bend of the part as SOLIDWORKS reads it back now.")]
    public SheetMetalFacts Sheet { get; set; } = new SheetMetalFacts();

    [Description("The flat pattern's size before the change; absent when the cut list did not give it.")]
    public FlatSize? FlatBefore { get; set; }

    [Description("The flat pattern's size after the change; absent when the cut list does not give it.")]
    public FlatSize? FlatAfter { get; set; }

    [Required]
    [Description("The checks made, each as a sentence with what was expected and measured.")]
    public List<string> Checks { get; set; } = new List<string>();
}

[Description("The flat pattern's size: the rectangle of sheet it is cut from.")]
public sealed class FlatSize
{
    [Description("The rectangle's length (its longer side), mm.")]
    public double LengthMm { get; set; }

    [Description("The rectangle's width, mm.")]
    public double WidthMm { get; set; }

    [Description("How many bends the cut list counts.")]
    public int Bends { get; set; }
}

[Description("The sheet-metal facts a sheet-metal operation reads back after it.")]
public sealed class SheetMetalFacts
{
    [Description("The sheet's thickness, mm.")]
    public double ThicknessMm { get; set; }

    [Description("folded or flattened.")]
    public SheetState State { get; set; }

    [Required]
    [Description("The bends this operation made (for sheetmetal.set_parameters, every bend of the part), as SOLIDWORKS reads them back.")]
    public List<BendInfo> Bends { get; set; } = new List<BendInfo>();
}
