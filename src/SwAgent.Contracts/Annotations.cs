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
[Description("Brings the models' own annotations onto the drawing's views (IDrawingDoc.InsertModelAnnotations3 into every view, the path CADapter measured reliable; into one view SOLIDWORKS picks the view itself): their dimensions when types is absent, and their hole callouts, datums, geometric tolerances and notes when types names them. With views, only those views keep what was brought: what SOLIDWORKS put into any other view is deleted again (removed counts it). Checked by counting what each view gained (never SOLIDWORKS' answer), and each imported dimension must be one of the model's (D1@Sketch2) whose value on the drawing equals its value in the model within 0.001 mm (0.01° for an angle). A model dimension shown in two views is reported under duplicates, not removed. The models are unchanged: changing a model dimension's value is dimension.set on the model, never done from the drawing. Otherwise everything it brought is deleted again (rolled back). Positions are sheet mm; values model mm." + DrawingChangeText.Waits)]
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

[Operation("annotation.dimension", typeof(AnnotationDimensionResult))]
[Description("Adds a reference (driven) dimension to a view between one or two model entities the view shows, each a ViewEntity (a reference issued in the view's model, a {component, entity} pair, or a name) selected through the view (IView.SelectEntity), never picked by sheet coordinates. kind: linear (a straight edge's length; the distance between two parallel edges; a vertex's or a circle's centre's distance from an edge; or between two of those), horizontal or vertical (an edge's run across or up the sheet; or between vertices, circle centres and edges square to that direction), radius or diameter (one circular edge the view draws as a circle or arc), or angle (two straight edges that are not parallel). Its text goes at at (sheet mm). Checked by measuring: its value equals the same quantity measured on the model between those entities as the view draws them (through the view's transform, divided by its scale, so model mm) within 0.001 mm, or within 0.01° for an angle (the angle or its supplement, whichever SOLIDWORKS dimensions); it is driven, a reference dimension of the drawing; and the model is unchanged. An entity the view does not show is refused naming the view and the entity. Otherwise the dimension is deleted again (rolled back). A model dimension's value is changed with dimension.set on the model, never from the drawing." + DrawingChangeText.Waits)]
public sealed class AnnotationDimensionRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("linear, horizontal, vertical, radius, diameter or angle.")]
    public DimensionKind Kind { get; set; }

    [Required]
    [Description("One or two entities of one view: edges or vertices it shows (view.entities lists them).")]
    public List<ViewEntity> Entities { get; set; } = new List<ViewEntity>();

    [Required]
    [Description("Where its text goes, sheet mm from the sheet's lower-left corner.")]
    public SheetPoint At { get; set; } = new SheetPoint();
}

[Description("The reference dimension made.")]
public sealed class AnnotationDimensionResult : IDrawingChangeResult
{
    [Required]
    [Description("The dimension, as read back.")]
    public AnnotationInfo Annotation { get; set; } = new AnnotationInfo();

    [Description("Its value as read back: model mm, or degrees for an angle.")]
    public double Value { get; set; }

    [Description("The same quantity measured on the model, which its value equals.")]
    public double ModelValue { get; set; }

    [Required]
    [Description("What was measured on the model: the distance between the two parallel lines, the circle's diameter, ….")]
    public string Measured { get; set; } = "";

    [Required]
    [Description("What changed in the drawing: created holds the dimension; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Operation("annotation.note", typeof(AnnotationNoteResult))]
[Description("Adds a note (IModelDoc2.InsertNote): on a sheet, or in a view, with a leader to a model entity the view shows when attach_to names one (a ViewEntity selected through its view, never picked by sheet coordinates), its text placed at at (sheet mm). text may link properties: $PRP:\"Title\" shows the drawing's Title (drawing.properties writes them), and shown gives the text as resolved. Checked: the note is on the sheet or in the view asked, its text reads back as written, its position within 0.01 mm, and its leader is attached to that entity; the model is unchanged. An entity the view does not show is refused naming the view and the entity. Otherwise the note is deleted again (rolled back)." + DrawingChangeText.Waits)]
public sealed class AnnotationNoteRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The note's text; $PRP:\"name\" links a property of the drawing.")]
    public string Text { get; set; } = "";

    [Required]
    [Description("Where its text goes, sheet mm from the sheet's lower-left corner.")]
    public SheetPoint At { get; set; } = new SheetPoint();

    [Description("The model entity its leader points at, as a view shows it: an edge, vertex or face. The note goes into that view.")]
    public ViewEntity? AttachTo { get; set; }

    [Description("The view the note goes into (a reference or a name), without a leader unless attach_to is given (then it must be attach_to's view). On the sheet itself when view and attach_to are absent.")]
    public string? View { get; set; }

    [Description("The sheet for a note on a sheet itself (a reference or a name); the sheet shown when absent. With view or attach_to, it must be that view's sheet.")]
    public string? Sheet { get; set; }
}

[Description("The note made.")]
public sealed class AnnotationNoteResult : IDrawingChangeResult
{
    [Required]
    [Description("The note, as read back.")]
    public AnnotationInfo Annotation { get; set; } = new AnnotationInfo();

    [Required]
    [Description("Its text as written, links unresolved.")]
    public string Text { get; set; } = "";

    [Required]
    [Description("Its text as the drawing shows it, linked properties resolved.")]
    public string Shown { get; set; } = "";

    [Required]
    [Description("What changed in the drawing: created holds the note; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Operation("annotation.centre_marks", typeof(AnnotationCentresResult))]
[Description("Adds centre marks to a view's circles (IDrawingDoc.InsertCenterMark3, a single mark on each circular edge, selected through the view, never picked by sheet coordinates): the circular edges named (each a ViewEntity of this view: a reference issued in its model, a {component, entity} pair, or a name), or, when entities is absent, every circle a view of a part shows, one mark per centre (a hole's front and back edges share one). A circle that already has a centre mark at its centre is skipped (already). Checked by walking the view's annotations, not by SOLIDWORKS' count (CADapter measured it answering 0 with marks in place): the view gained one centre mark per circle, each at its circle's centre within 0.01 mm (sheet mm); the model is unchanged. Otherwise the marks are deleted again (rolled back)." + DrawingChangeText.Waits)]
public sealed class AnnotationCentreMarksRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("The circular edges to mark, each as this view shows it (the view draws it as a circle or arc). Every circle the view shows when absent.")]
    public List<ViewEntity>? Entities { get; set; }
}

[Operation("annotation.centre_lines", typeof(AnnotationCentresResult))]
[Description("Adds centre lines to a view (IDrawingDoc.InsertCenterLine2, what each runs along selected through the view, never picked by sheet coordinates): along each cylindrical face named that the view sees from the side, or between exactly two straight edges named that it draws parallel (each a ViewEntity of this view: a reference issued in its model, a {component, entity} pair, or a name); or, when entities is absent, along every cylinder a view of a part shows from the side, one per axis (SOLIDWORKS splits a hole's cylinder in two faces on one axis). An axis that already has a centre line is skipped (already). Checked by walking the view's annotations: the view gained one centre line per target, each attached to the face, or the two edges, it was made on; the model is unchanged. Otherwise the centre lines are deleted again (rolled back)." + DrawingChangeText.Waits)]
public sealed class AnnotationCentreLinesRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("Cylindrical faces this view sees from the side (one centre line each), or exactly two straight edges it draws parallel (one centre line between them). Every cylinder the view shows from the side when absent.")]
    public List<ViewEntity>? Entities { get; set; }
}

[Description("The centre marks or centre lines made.")]
public sealed class AnnotationCentresResult : IDrawingChangeResult
{
    [Required]
    [Description("Each one made, as read back.")]
    public List<AnnotationInfo> Made { get; set; } = new List<AnnotationInfo>();

    [Description("How many targets there were: circles (one per centre) or axes, named or found in the view.")]
    public int Eligible { get; set; }

    [Description("How many of those already had one, and were skipped.")]
    public int Already { get; set; }

    [Required]
    [Description("What changed in the drawing: created holds each one made; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Operation("annotation.datum", typeof(AnnotationResult))]
[Description("Adds a datum tag (IModelDoc2.InsertDatumTag2) to a model entity a view shows, an edge (a face seen edge on) or a face, selected through its view (attach_to, a ViewEntity), never picked by sheet coordinates (SOLIDWORKS 2026 SP4 no longer finds a drawing edge by sheet coordinates for a datum tag). label is 1 to 3 letters A to Z without I, O and Q, and must not already label a datum tag of the drawing; at (sheet mm) moves the tag. Checked: the tag reads back its label, is attached to that entity, and is at at within 0.01 mm when given; the model is unchanged. An entity the view does not show is refused naming the view and the entity. Otherwise the tag is deleted again (rolled back)." + DrawingChangeText.Waits)]
public sealed class AnnotationDatumRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The datum's label: 1 to 3 letters A to Z without I, O and Q (A, B, AA).")]
    public string Label { get; set; } = "";

    [Required]
    [Description("The edge or face the tag is attached to, as a view shows it.")]
    public ViewEntity AttachTo { get; set; } = new ViewEntity();

    [Description("Where the tag goes, sheet mm. Where SOLIDWORKS puts it when absent.")]
    public SheetPoint? At { get; set; }
}

[Operation("annotation.gtol", typeof(AnnotationResult))]
[Description("Adds a single geometric tolerance frame (IModelDoc2.InsertGtol) to a model entity a view shows, an edge or a face, selected through its view (attach_to, a ViewEntity), never picked by sheet coordinates: characteristic one of the fourteen (straightness … total_runout), tolerance_mm (mm, more than 0) with the diameter symbol when diameter is true and material_condition none, mmc or lmc, and up to three datums, primary first, each the label of a datum tag already on the drawing (annotation.datum makes them; a form tolerance, straightness, flatness, circularity or cylindricity, takes none). The frame is set with IGtol.SetFrameSymbols (SOLIDWORKS' numbers; CADapter measured the string variant wanting font codes) and SetFrameValues2; at (sheet mm) moves it. Checked: the frame reads back its characteristic, tolerance, diameter symbol, material condition and datums, is attached to that entity, and is at at within 0.01 mm when given; the model is unchanged. Otherwise the frame is deleted again (rolled back). Composite and multi-frame tolerances are out of scope." + DrawingChangeText.Waits)]
public sealed class AnnotationGtolRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("straightness, flatness, circularity, cylindricity, profile_of_a_line, profile_of_a_surface, angularity, perpendicularity, parallelism, position, concentricity, symmetry, circular_runout or total_runout.")]
    public GtolCharacteristic Characteristic { get; set; }

    [Description("The tolerance, mm, more than 0.")]
    public double ToleranceMm { get; set; }

    [Required]
    [Description("The edge or face the frame is attached to, as a view shows it.")]
    public ViewEntity AttachTo { get; set; } = new ViewEntity();

    [Description("Up to three datum labels, primary first, each a datum tag's already on the drawing. None when absent.")]
    public List<string>? Datums { get; set; }

    [Description("True to give the tolerance the diameter symbol (Ø), a cylindrical tolerance zone.")]
    public bool? Diameter { get; set; }

    [Description("none, mmc or lmc; none when absent.")]
    public MaterialCondition? MaterialCondition { get; set; }

    [Description("Where the frame goes, sheet mm. Where SOLIDWORKS puts it when absent.")]
    public SheetPoint? At { get; set; }
}

[Operation("annotation.list", typeof(AnnotationListResult), ReadOnly = true)]
[Description("Lists a drawing's annotations (IView.GetAnnotations): every sheet's own and every view's, or those on one sheet (its own and its views'), or one view's. Each with its reference, kind (dimension, note, centre_mark, centre_line, datum, gtol, other), view, position (sheet mm), a note's text as written and as shown, a dimension's kind, value (model mm, or degrees), name and whether it is one of the model's or a reference dimension of the drawing, a datum's label, a tolerance frame's contents, and the model entities it is attached to as references issued in its view's model. Changes nothing.")]
public sealed class AnnotationListRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Only the annotations on this sheet, its own and its views' (a reference or a name).")]
    public string? Sheet { get; set; }

    [Description("Only this view's annotations (a reference or a name).")]
    public string? View { get; set; }
}

[Description("The drawing's annotations.")]
public sealed class AnnotationListResult
{
    [Required]
    [Description("Each annotation, sheet by sheet: the sheet's own, then each view's.")]
    public List<AnnotationInfo> Annotations { get; set; } = new List<AnnotationInfo>();
}

[Operation("annotation.set", typeof(AnnotationResult))]
[Description("Moves an annotation's text to at (sheet mm; IAnnotation.SetPosition2), or gives a note new text (INote.SetText; $PRP:\"…\" links allowed). Each value is recorded before and put back on failure. Checked: the position reads back within 0.01 mm and the text as written; the model is unchanged. text is for notes only: a model dimension's value is changed with dimension.set on the model, never from the drawing." + DrawingChangeText.Waits)]
public sealed class AnnotationSetRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The annotation: a reference (r1.annotation.…), or its name (with @view when names repeat).")]
    public string Annotation { get; set; } = "";

    [Description("Where its text goes, sheet mm.")]
    public SheetPoint? At { get; set; }

    [Description("A note's new text.")]
    public string? Text { get; set; }
}

[Operation("annotation.delete", typeof(AnnotationDeleteResult))]
[Description("Deletes annotations by selecting each (IAnnotation.Select3, its sheet shown) and deleting the selection (IModelDoc2.EditDelete), a view or sheet at a time. Deleting an imported model dimension removes it from the drawing only: the model keeps it, with its value (checked). Checked: each is gone, every other annotation is still there, and the model is unchanged. Not rolled back (a deleted annotation cannot be made again as it was): a failed check says what is left, and when SOLIDWORKS refuses one view's annotations after deleting another's, operation_failed gives what is left with rollback_failed. A reference to a deleted annotation no longer resolves." + DrawingChangeText.Waits)]
public sealed class AnnotationDeleteRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The annotations to delete: each a reference (r1.annotation.…) or a name (with @view when names repeat).")]
    public List<string> Annotations { get; set; } = new List<string>();
}

[Description("The annotations deleted.")]
public sealed class AnnotationDeleteResult : IDrawingChangeResult
{
    [Required]
    [Description("Each annotation deleted, as name@view (or name@sheet for one on a sheet itself).")]
    public List<string> Deleted { get; set; } = new List<string>();

    [Required]
    [Description("Each model dimension deleted from the drawing that the model keeps, as D1@Sketch2 = 20 (model mm, or degrees).")]
    public List<string> ModelDimensionsKept { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the drawing: changed lists each annotation deleted; annotations before and after; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Description("A custom property to write: its name, and its value as text.")]
public sealed class DrawingPropertyValue
{
    [Required]
    [Description("The property's name, for example Title, PartNo, Material or DrawnBy.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("Its value as text; $PRP:\"name\" links another property of the drawing.")]
    public string Value { get; set; } = "";
}

[Description("A custom property of the drawing.")]
public sealed class DrawingProperty
{
    [Required]
    [Description("Its name.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("Its value as written, links unresolved.")]
    public string Value { get; set; } = "";

    [Required]
    [Description("Its value as SOLIDWORKS resolves it, which the title block shows.")]
    public string Resolved { get; set; } = "";
}

[Operation("drawing.properties", typeof(DrawingPropertiesResult))]
[Description("Reads the drawing's own custom properties, which its title block shows (a title, a part number, a material, a drafter), and with set writes them first, each as text (ICustomPropertyManager.Set2 for one it has, Add3 for a new one); a value may link another property ($PRP:\"PartNo\"). Read back through ICustomPropertyManager.Get2, the getter CADapter measured resolving where Get5 does not. Checked: each property written reads back as written, and resolved gives its value as shown. On failure each old value is put back and each new property deleted (rolled back). The drawing's own properties only: not a configuration's, not the model's; the title block's geometry is not edited." + DrawingChangeText.Waits)]
public sealed class DrawingPropertiesRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The properties to write first, each by name (a name it has is overwritten). Nothing is written when absent.")]
    public List<DrawingPropertyValue>? Set { get; set; }
}

[Description("The drawing's custom properties.")]
public sealed class DrawingPropertiesResult : IDrawingChangeResult
{
    [Required]
    [Description("Every custom property of the drawing, as read back after writing.")]
    public List<DrawingProperty> Properties { get; set; } = new List<DrawingProperty>();

    [Required]
    [Description("The names of the properties written.")]
    public List<string> Written { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the drawing: changed holds each property written, its value before (empty for a new one) and after; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Description("The annotation made or changed.")]
public sealed class AnnotationResult : IDrawingChangeResult
{
    [Required]
    [Description("The annotation, as read back.")]
    public AnnotationInfo Annotation { get; set; } = new AnnotationInfo();

    [Required]
    [Description("What changed in the drawing: created holds an annotation made, changed each value set; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}
