using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("What kind of annotation it is.")]
public enum AnnotationKind
{
    [Description("A dimension: one of the model's, imported (annotation.import), or a reference dimension made on the drawing (annotation.dimension).")]
    Dimension,

    [Description("A note.")]
    Note,

    [Description("A centre mark on a circle.")]
    CentreMark,

    [Description("A centre line along a cylinder's axis or between two edges.")]
    CentreLine,

    [Description("A datum tag.")]
    Datum,

    [Description("A geometric tolerance frame.")]
    Gtol,

    [Description("Any other kind (a balloon, a surface finish or weld symbol, a hole callout): listed and deleted, never made here.")]
    Other,
}

[Description("A tolerance frame's geometric characteristic, as ISO 1101 and ASME Y14.5 name the fourteen.")]
public enum GtolCharacteristic
{
    [Description("Straightness (a form tolerance: no datums).")]
    Straightness,

    [Description("Flatness (a form tolerance: no datums).")]
    Flatness,

    [Description("Circularity, or roundness (a form tolerance: no datums).")]
    Circularity,

    [Description("Cylindricity (a form tolerance: no datums).")]
    Cylindricity,

    [Description("Profile of a line.")]
    ProfileOfALine,

    [Description("Profile of a surface.")]
    ProfileOfASurface,

    [Description("Angularity.")]
    Angularity,

    [Description("Perpendicularity.")]
    Perpendicularity,

    [Description("Parallelism.")]
    Parallelism,

    [Description("Position.")]
    Position,

    [Description("Concentricity.")]
    Concentricity,

    [Description("Symmetry.")]
    Symmetry,

    [Description("Circular runout.")]
    CircularRunout,

    [Description("Total runout.")]
    TotalRunout,
}

[Description("A tolerance's material condition modifier.")]
public enum MaterialCondition
{
    [Description("No modifier (regardless of feature size).")]
    None,

    [Description("Maximum material condition (Ⓜ).")]
    Mmc,

    [Description("Least material condition (Ⓛ).")]
    Lmc,
}

[Description("What a geometric tolerance frame says, as made or read back.")]
public sealed class GtolFrame
{
    [Description("Its geometric characteristic; absent when SOLIDWORKS gives one the add-in does not name.")]
    public GtolCharacteristic? Characteristic { get; set; }

    [Description("Its tolerance, mm; absent when its text is not a number.")]
    public double? ToleranceMm { get; set; }

    [Description("True when the tolerance carries the diameter symbol (Ø): a cylindrical tolerance zone.")]
    public bool Diameter { get; set; }

    [Description("The tolerance's material condition; absent when SOLIDWORKS gives one the add-in does not name.")]
    public MaterialCondition? MaterialCondition { get; set; }

    [Required]
    [Description("Its datum references, primary first (up to three); empty for none.")]
    public List<string> Datums { get; set; } = new List<string>();
}

[Description("An annotation on a drawing, as annotation.list and the annotation operations report it. Positions in sheet mm; dimension values in model mm (or degrees).")]
public sealed class AnnotationInfo
{
    [Required]
    [Description("Its name in its view (or on its sheet), as SOLIDWORKS names it.")]
    public string Name { get; set; } = "";

    [Description("Its reference (r1.annotation.…). It names the annotation by its sheet, view and name: one deleted, or whose view is renamed or deleted, no longer resolves. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("dimension, note, centre_mark, centre_line, datum, gtol or other.")]
    public AnnotationKind Kind { get; set; }

    [Required]
    [Description("The sheet it is on.")]
    public string Sheet { get; set; } = "";

    [Description("The view it belongs to; absent for one on the sheet itself.")]
    public string? View { get; set; }

    [Description("Where it is on the sheet, sheet mm (its text's position); absent when SOLIDWORKS gives none.")]
    public SheetPoint? Position { get; set; }

    [Description("A note's text as shown, with linked properties resolved; absent for other kinds.")]
    public string? Text { get; set; }

    [Description("A note's text as written, with any $PRP:\"…\" links unresolved; absent for other kinds.")]
    public string? LinkedText { get; set; }

    [Description("A dimension's kind: linear, horizontal, vertical, radius, diameter or angle; absent for another kind of dimension, or another annotation.")]
    public DimensionKind? DimensionKind { get; set; }

    [Description("A dimension's value: model mm, or degrees for an angle.")]
    public double? Value { get; set; }

    [Description("A dimension's name: the model's (D1@Sketch2) for a model dimension, the drawing's (RD1@Drawing View1) for a reference dimension.")]
    public string? DimensionName { get; set; }

    [Description("For a dimension: true for one of the model's (it drives the model, and is changed with dimension.set on the model), false for a reference dimension made on the drawing.")]
    public bool? ModelDimension { get; set; }

    [Description("A datum tag's label.")]
    public string? Label { get; set; }

    [Description("A geometric tolerance frame's contents.")]
    public GtolFrame? Gtol { get; set; }

    [Required]
    [Description("The model entities it is attached to, each as its view shows it (view and the reference issued in the view's model; reference is absent when SOLIDWORKS gives none). Empty when it is attached to none.")]
    public List<ViewEntity> Attached { get; set; } = new List<ViewEntity>();
}
