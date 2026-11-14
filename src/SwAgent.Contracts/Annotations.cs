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

[Description("A kind of the models' own annotations that annotation.import brings onto a drawing.")]
public enum AnnotationImportType
{
    [Description("The models' dimensions, whether marked for drawing or not.")]
    Dimensions,

    [Description("Hole Wizard holes' callouts.")]
    HoleCallouts,

    [Description("The models' datum features.")]
    Datums,

    [Description("The models' geometric tolerances.")]
    Gtols,

    [Description("The models' notes.")]
    Notes,
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

[Operation("annotation.import", typeof(AnnotationImportResult))]
[Description("Brings the models' own annotations onto the drawing's views (IDrawingDoc.InsertModelAnnotations3 into every view, the path CADapter measured reliable; into one view SOLIDWORKS picks the view itself): their dimensions when types is absent, and their hole callouts, datums, geometric tolerances and notes when types names them. With views, only those views keep what was brought: what SOLIDWORKS put into any other view is deleted again (removed counts it). Checked by counting what each view gained (never SOLIDWORKS' answer), and each imported dimension must be one of the model's (D1@Sketch2) whose value on the drawing equals its value in the model within 0.001 mm (0.01° for an angle). A model dimension shown in two views is reported under duplicates, not removed. The models are unchanged: changing a model dimension's value is dimension.set on the model, never done from the drawing. Otherwise everything it brought is deleted again (rolled back). Positions are sheet mm; values model mm.")]
public sealed class AnnotationImportRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The views to bring them into: each a reference (r1.view.…) or a name (Drawing View1). Every view when absent.")]
    public List<string>? Views { get; set; }

    [Description("What to bring: dimensions, hole_callouts, datums, gtols, notes. Dimensions when absent.")]
    public List<AnnotationImportType>? Types { get; set; }
}

[Description("A model dimension brought onto the drawing, checked against the model.")]
public sealed class ImportedDimension
{
    [Required]
    [Description("The annotation's name in its view.")]
    public string Annotation { get; set; } = "";

    [Description("The annotation's reference (r1.annotation.…).")]
    public string? Reference { get; set; }

    [Required]
    [Description("The view it was brought into.")]
    public string View { get; set; } = "";

    [Required]
    [Description("The model's name for it, as dimension.set takes it (D1@Sketch2).")]
    public string Dimension { get; set; } = "";

    [Description("Its value on the drawing: model mm, or degrees for an angle.")]
    public double Value { get; set; }

    [Description("Its value in the model, which the drawing's equals.")]
    public double ModelValue { get; set; }

    [Description("True for an angle (degrees).")]
    public bool Angle { get; set; }
}

[Description("What annotation.import brought.")]
public sealed class AnnotationImportResult : IDrawingChangeResult
{
    [Required]
    [Description("Each annotation brought, as read back.")]
    public List<AnnotationInfo> Annotations { get; set; } = new List<AnnotationInfo>();

    [Required]
    [Description("Each model dimension brought, its value on the drawing checked against the model's.")]
    public List<ImportedDimension> Dimensions { get; set; } = new List<ImportedDimension>();

    [Required]
    [Description("Model dimensions shown in more than one view, each as \"D1@Sketch2 in Drawing View1, Drawing View2\": reported, not removed.")]
    public List<string> Duplicates { get; set; } = new List<string>();

    [Description("How many annotations SOLIDWORKS put into views not asked for, which were deleted again.")]
    public int Removed { get; set; }

    [Required]
    [Description("What changed in the drawing: created holds each annotation brought; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}
