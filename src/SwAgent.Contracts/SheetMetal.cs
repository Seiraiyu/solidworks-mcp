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
    [Description("How it develops when flattened.")]
    public BendAllowance Allowance { get; set; } = new BendAllowance();

    [Description("True when its radius is the part's default.")]
    public bool DefaultRadius { get; set; }

    [Description("True when its allowance is the part's default.")]
    public bool DefaultAllowance { get; set; }

    [Description("How long it is along its axis, mm; absent when its axis is not along x, y or z.")]
    public double? WidthMm { get; set; }
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
