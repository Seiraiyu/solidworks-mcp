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

    [Required]
    [Description("The faces the feature made, each with its reference and geometry.")]
    public List<EntityInfo> Faces { get; set; } = new List<EntityInfo>();

    [Required]
    [Description("The sheet's thickness and state, and the bends this feature made.")]
    public SheetMetalFacts Sheet { get; set; } = new SheetMetalFacts();

    [Required]
    [Description("The exact checks made beyond the common ones, each as a sentence with what was expected and measured.")]
    public List<string> Checks { get; set; } = new List<string>();
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
