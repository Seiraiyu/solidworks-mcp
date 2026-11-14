using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// An open drawing, as the drawing operations read and change it, so they can be tested without SOLIDWORKS (design 611).
/// UI thread only. Sheets and views are named by their drawing-unique names, which is how every SOLIDWORKS call that takes
/// them names them (SelectByID2 "SHEET" and "DRAWINGVIEW", ActivateSheet, the Sheet property). Positions and sizes are
/// sheet millimetres from the sheet's lower-left corner; model entities are SOLIDWORKS' own objects of the view's model.
/// </summary>
public interface ISwDrawing
{
    /// <summary>Every sheet's name, in SOLIDWORKS' order.</summary>
    IReadOnlyList<string> SheetNames();

    /// <summary>The sheet shown now, or null when SOLIDWORKS names none.</summary>
    string? ActiveSheet();

    /// <summary>A sheet's size, scale, projection and format, read now; null when there is no sheet of that name.</summary>
    SheetFacts? ReadSheet(string name);

    /// <summary>True while the user edits a sheet format (Edit Sheet Format) rather than the sheet (IDrawingDoc.GetEditSheet false).</summary>
    bool EditingSheetFormat();

    /// <summary>
    /// Adds a sheet with these values (IDrawingDoc.NewSheet4; a standard paper code when the size is one, else a custom
    /// size; <see cref="SheetFacts.Format"/> drawn in, or no format when null); false when SOLIDWORKS made none. The new
    /// sheet is made active.
    /// </summary>
    bool AddSheet(SheetFacts sheet);

    /// <summary>
    /// Gives an existing sheet these values: size and projection through ISheet.SetProperties2, the scale through
    /// ISheet.SetScale, the format through SetTemplateName and ReloadTemplate when it changed, and the name through SetName
    /// when it changed. Nothing is checked here: the caller reads the sheet back.
    /// </summary>
    void SetSheet(string name, SheetFacts sheet);

    /// <summary>Shows a sheet (IDrawingDoc.ActivateSheet); false when SOLIDWORKS refused.</summary>
    bool ActivateSheet(string name);

    /// <summary>Deletes a sheet and its views by selecting it (SelectByID2 "SHEET") and DeleteSelection2; false when SOLIDWORKS refused.</summary>
    bool DeleteSheet(string name);

    /// <summary>The names of a sheet's views, in SOLIDWORKS' order (the sheet's own view not included); empty for no such sheet.</summary>
    IReadOnlyList<string> ViewNames(string sheet);

    /// <summary>A view as SOLIDWORKS holds it now, without its drawn extents; null when no view has that name.</summary>
    ViewFacts? ReadView(string name);

    /// <summary>The view's model-to-sheet transform (IView.ModelToViewTransform), in mm; null when SOLIDWORKS gives none.</summary>
    ViewTransform? Transform(string view);

    /// <summary>IView.GetPolylines5(1)'s numbers, as SOLIDWORKS gives them (see <see cref="ViewGeometry.DrawnExtents"/>).</summary>
    IReadOnlyList<double> Polylines(string view);

    /// <summary>
    /// A model view of <paramref name="modelPath"/> in an orientation design 611 names (front, isometric; SOLIDWORKS' *Front, *Isometric)
    /// or the model's named view centred near <paramref name="at"/> on the active sheet (IDrawingDoc.CreateDrawViewFromModelView3): its
    /// name, or null when SOLIDWORKS made none. SOLIDWORKS loads the model, hidden, when it is not open.
    /// </summary>
    string? CreateModelView(string modelPath, string orientation, SheetPoint at);

    /// <summary>
    /// A view projected from <paramref name="parent"/> placed at <paramref name="at"/>, aligned with it, by selecting the
    /// parent (SelectByID2 "DRAWINGVIEW") and IDrawingDoc.CreateUnfoldedViewAt3: its name, or null when SOLIDWORKS made none.
    /// </summary>
    string? ProjectView(string parent, SheetPoint at);

    /// <summary>Moves a view's centre to <paramref name="at"/> (IView.Position).</summary>
    void SetPosition(string view, SheetPoint at);

    /// <summary>Draws a view at <paramref name="scale"/> (IView.ScaleRatio), or at its sheet's scale when null (IView.UseSheetScale).</summary>
    void SetScale(string view, ScaleRatio? scale);

    /// <summary>Draws a view in <paramref name="display"/> (IView.SetDisplayMode4).</summary>
    void SetDisplay(string view, ViewDisplay display);

    /// <summary>Makes a view show another configuration of its model (IView.ReferencedConfiguration).</summary>
    void SetConfiguration(string view, string configuration);

    /// <summary>
    /// Deletes views by selecting each (SelectByID2 "DRAWINGVIEW", appended) and DeleteSelection2, a sheet at a time with
    /// that sheet shown; false when SOLIDWORKS refused, in which case the views on sheets done before are already gone.
    /// </summary>
    bool DeleteViews(IReadOnlyList<string> views);

    /// <summary>The document of the model a view shows (IView.ReferencedDocument); null when it shows none or it is not loaded.</summary>
    ISwDocument? ModelOf(string view);

    /// <summary>
    /// The model's edges, silhouette edges and vertices a view shows (IView.GetVisibleEntities2 with no component), as the
    /// view's model's own objects, each once.
    /// </summary>
    IReadOnlyList<VisibleEntity> VisibleEntities(string view, bool edges, bool vertices);

    /// <summary>
    /// A full section view of <paramref name="parent"/> centred near <paramref name="at"/>: the add-in draws a line from
    /// <paramref name="from"/> to <paramref name="to"/> (sheet mm) in the parent's own sketch (the parent activated; the
    /// points converted with ViewCutGeometry.ToViewSketch, which CADapter measured: a line in sheet coordinates cuts
    /// nothing and reports success), selects that line, and calls IDrawingDoc.CreateSectionViewAt5 with the label, the
    /// cut's direction reversed when asked, aligned, and no excluded components (null, never an array). Its name, or null
    /// when SOLIDWORKS made none, in which case the line is deleted again.
    /// </summary>
    string? CutSection(string parent, SheetPoint from, SheetPoint to, SheetPoint at, string label, bool reversed);

    /// <summary>The section a section view shows (IView.GetSection): its label, whether its cut is reversed, and its section line's name; null for any other view.</summary>
    SectionFacts? ReadSection(string view);

    /// <summary>Each face a section view hatches (IView.GetFaceHatches, IFaceHatch.Face), model mm; empty when it hatches none.</summary>
    IReadOnlyList<CutFace> CutFaces(string view);

    /// <summary>The labels of the section lines and detail circles <paramref name="view"/> holds (IView.GetSectionLines, GetDetailCircles): the views cut or detailed from it.</summary>
    IReadOnlyList<string> CutLabels(string view);

    /// <summary>
    /// Deletes the section line labelled <paramref name="label"/> that <paramref name="parent"/> holds by selecting it
    /// (SelectByID2 "SECTIONLINE" with its name) and DeleteSelection2. Nothing to do when it holds none; false when
    /// SOLIDWORKS refused.
    /// </summary>
    bool DeleteSectionLine(string parent, string label);

    /// <summary>
    /// A detail view of <paramref name="parent"/> centred near <paramref name="at"/> at <paramref name="scale"/>: the add-in
    /// draws a circle centred at <paramref name="centre"/>, <paramref name="radiusMm"/> in radius (both sheet mm, as drawn
    /// in the parent) in the parent's own sketch (as for <see cref="CutSection"/>), selects it, and calls
    /// IDrawingDoc.CreateDetailViewAt4 with the label, a standard style and a full circular outline. Its name, or null
    /// when SOLIDWORKS made none, in which case the circle is deleted again.
    /// </summary>
    string? DetailView(string parent, SheetPoint centre, double radiusMm, SheetPoint at, ScaleRatio scale, string label);

    /// <summary>The detail a detail view shows (IView.GetDetail): its label, its circle's name and where the parent draws the circle's centre; null for any other view.</summary>
    DetailFacts? ReadDetail(string view);

    /// <summary>
    /// Deletes the detail circle labelled <paramref name="label"/> that <paramref name="parent"/> holds by selecting it
    /// (IDetailCircle.Select) and DeleteSelection2. Nothing to do when it holds none; false when SOLIDWORKS refused.
    /// </summary>
    bool DeleteDetailCircle(string parent, string label);

    /// <summary>
    /// A flat-pattern view of the sheet-metal part at <paramref name="modelPath"/> in <paramref name="configuration"/>,
    /// centred near <paramref name="at"/> on the active sheet (IDrawingDoc.CreateFlatPatternViewFromModelView3, bend
    /// lines shown, not flipped): its name, or null when SOLIDWORKS made none (CADapter: it makes none of a part that is
    /// not open).
    /// </summary>
    string? FlatPatternView(string modelPath, string configuration, SheetPoint at);

    /// <summary>How many bend lines a flat-pattern view draws (IView.GetBendLineCount).</summary>
    int BendLines(string view);

    /// <summary>Whether a flat-pattern view shows its bend notes (IView.ShowSheetMetalBendNotes).</summary>
    bool BendNotes(string view);

    /// <summary>Shows or hides a flat-pattern view's bend notes (IView.ShowSheetMetalBendNotes).</summary>
    void SetBendNotes(string view, bool show);

    /// <summary>
    /// The annotations of a view, or of the sheet itself when <paramref name="view"/> is null (its sheet view's), as
    /// SOLIDWORKS holds them now (IView.GetAnnotations): each with its name, kind, position, what its kind carries and the
    /// model entities it is attached to (IAnnotation.GetAttachedEntities3). Empty for no such sheet or view.
    /// </summary>
    IReadOnlyList<AnnotationFacts> Annotations(string sheet, string? view);

    /// <summary>
    /// Brings the models' annotations of these types into every view that shows them (IDrawingDoc.InsertModelAnnotations3:
    /// the entire model, all views, no duplicates, no hidden features' items). Its answer is not evidence, so none is
    /// returned: the caller counts what the views gained.
    /// </summary>
    void ImportAnnotations(IReadOnlyList<AnnotationImportType> types);

    /// <summary>
    /// A reference dimension of <paramref name="kind"/> between <paramref name="entities"/> (the view's model's own
    /// objects), each selected through the view (IView.SelectEntity, the view's sheet shown), with its text at
    /// <paramref name="at"/> (IModelDoc2.AddDimension2, AddHorizontalDimension2, AddVerticalDimension2,
    /// AddRadialDimension2 or AddDiameterDimension2): its annotation's name, or null when SOLIDWORKS made none.
    /// </summary>
    string? AddDimension(string view, DimensionKind kind, IReadOnlyList<object> entities, SheetPoint at);

    /// <summary>
    /// A note (IModelDoc2.InsertNote) moved to <paramref name="at"/> (IAnnotation.SetPosition2): with a leader to
    /// <paramref name="attachTo"/> selected through <paramref name="view"/>; else in the view, activated; else, with
    /// <paramref name="view"/> null, on the sheet with no view active. Its name, or null when SOLIDWORKS made none.
    /// </summary>
    string? AddNote(string sheet, string? view, string text, SheetPoint at, object? attachTo);

    /// <summary>A centre mark on a circular edge selected through the view (IDrawingDoc.InsertCenterMark3, a single mark): its name, or null.</summary>
    string? AddCentreMark(string view, object edge);

    /// <summary>A centre line on a face, or between two edges, selected through the view (IDrawingDoc.InsertCenterLine2): its name, or null.</summary>
    string? AddCentreLine(string view, IReadOnlyList<object> entities);

    /// <summary>A datum tag on an entity selected through the view (IModelDoc2.InsertDatumTag2), labelled (IDatumTag.SetLabel) and moved to <paramref name="at"/> when given: its name, or null.</summary>
    string? AddDatum(string view, object entity, string label, SheetPoint? at);

    /// <summary>
    /// A geometric tolerance frame on an entity selected through the view (IModelDoc2.InsertGtol), its first frame set
    /// (IGtol.SetFrameSymbols with SOLIDWORKS' numbers, then SetFrameValues2) and moved to <paramref name="at"/> when
    /// given: its name, or null.
    /// </summary>
    string? AddGtol(string view, object entity, GtolFrame frame, SheetPoint? at);

    /// <summary>Moves an annotation's text to <paramref name="at"/> (IAnnotation.SetPosition2). Nothing is checked here.</summary>
    void MoveAnnotation(string sheet, string? view, string name, SheetPoint at);

    /// <summary>Gives a note new text (INote.SetText); false when SOLIDWORKS refused or it is not a note.</summary>
    bool SetNoteText(string sheet, string? view, string name, string text);

    /// <summary>
    /// Deletes annotations of one view (or of the sheet itself) by selecting each (IAnnotation.Select3, appended) with
    /// its sheet shown, and IModelDoc2.EditDelete; false when one could not be selected. The caller reads what is left.
    /// </summary>
    bool DeleteAnnotations(string sheet, string? view, IReadOnlyList<string> names);

    /// <summary>
    /// True when the view shows <paramref name="entity"/>: it is one of the edges, silhouette edges, vertices or faces
    /// IView.GetVisibleEntities2 gives (for <paramref name="component"/> in an assembly view), by ISldWorks.IsSame.
    /// </summary>
    bool Shows(string view, object entity, object? component);

    /// <summary>The model faces a view of a part shows (IView.GetVisibleEntities2, faces), as the part's own objects.</summary>
    IReadOnlyList<object> VisibleFaces(string view);

    /// <summary>True when two SOLIDWORKS objects are the same entity (ISldWorks.IsSame).</summary>
    bool SameEntity(object a, object b);

    /// <summary>The drawing's own custom properties (not a configuration's), each as written and resolved (ICustomPropertyManager.Get2).</summary>
    IReadOnlyList<PropertyFacts> Properties();

    /// <summary>Writes a custom property of the drawing as text: Set2 for one it has, Add3 for a new one. False when SOLIDWORKS refused.</summary>
    bool SetProperty(string name, string value);

    /// <summary>Deletes a custom property of the drawing (Delete2); false when SOLIDWORKS refused.</summary>
    bool DeleteProperty(string name);

    /// <summary>
    /// The drawing's own document settings the agent sets on a drawing it creates (design 611, Session preferences: origins,
    /// planes, axes, sketches and points hidden in its views; nothing inserted automatically when a view is made), by the
    /// names in <see cref="DrawingSettings.All"/>, read now.
    /// </summary>
    IReadOnlyDictionary<string, bool> Settings();

    /// <summary>Sets one of <see cref="DrawingSettings.All"/> on this drawing (IModelDocExtension.SetUserPreferenceToggle).</summary>
    void SetSetting(string name, bool value);
}

/// <summary>A sheet as SOLIDWORKS holds it, or the values to give one. Sheet mm.</summary>
public sealed class SheetFacts
{
    public string Name { get; set; } = "";

    public double WidthMm { get; set; }

    public double HeightMm { get; set; }

    public ScaleRatio Scale { get; set; } = new ScaleRatio();

    public Projection Projection { get; set; } = Projection.ThirdAngle;

    /// <summary>
    /// Its sheet format as SOLIDWORKS names it (ISheet.GetTemplateName): the full path of a .SLDDRT, a template's own format
    /// held in the drawing template with no file ("*.drt", <see cref="SheetSetups.InTemplate"/>), or null when it has none.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// SOLIDWORKS' template type for it (swDwgTemplates_e, ISheet.GetProperties2()[1]) as read, or null when not read from
    /// SOLIDWORKS; the type a new sheet in a template's own format is made with. Not compared.
    /// </summary>
    public int? Template { get; set; }

    public SheetFacts Copy() => new SheetFacts
    {
        Name = Name, WidthMm = WidthMm, HeightMm = HeightMm, Scale = new ScaleRatio(Scale.Numerator, Scale.Denominator), Projection = Projection, Format = Format,
        Template = Template,
    };
}

/// <summary>A drawing view as SOLIDWORKS holds it. Sheet mm.</summary>
public sealed class ViewFacts
{
    public string Name { get; set; } = "";

    public string Sheet { get; set; } = "";

    public ViewType Type { get; set; }

    /// <summary>Full path of the model it shows, or null.</summary>
    public string? Model { get; set; }

    public string? Configuration { get; set; }

    /// <summary>The orientation as design 611 names it (front, isometric), the model's named view as SOLIDWORKS names it, or null.</summary>
    public string? Orientation { get; set; }

    /// <summary>The view it was made from (IView.GetBaseView), or null.</summary>
    public string? Parent { get; set; }

    public SheetPoint Position { get; set; } = new SheetPoint();

    public ScaleRatio Scale { get; set; } = new ScaleRatio();

    /// <summary>True when it follows its sheet's scale (IView.UseSheetScale).</summary>
    public bool SheetScale { get; set; }

    /// <summary>Null for a display mode the add-in does not name.</summary>
    public ViewDisplay? Display { get; set; }

    public bool Exploded { get; set; }

    public int Annotations { get; set; }

    public int Tables { get; set; }

    public ViewFacts Copy() => new ViewFacts
    {
        Name = Name, Sheet = Sheet, Type = Type, Model = Model, Configuration = Configuration, Orientation = Orientation, Parent = Parent,
        Position = new SheetPoint(Position.X, Position.Y), Scale = new ScaleRatio(Scale.Numerator, Scale.Denominator), SheetScale = SheetScale,
        Display = Display, Exploded = Exploded, Annotations = Annotations, Tables = Tables,
    };
}

/// <summary>The section a section view shows, as SOLIDWORKS holds it.</summary>
public sealed class SectionFacts
{
    public string Label { get; set; } = "";

    /// <summary>True when its cut looks the other way from SOLIDWORKS' default (IDrSection.GetReversedCutDirection).</summary>
    public bool Reversed { get; set; }

    /// <summary>Its section line's name in its parent (IDrSection.GetName), which selects it.</summary>
    public string Line { get; set; } = "";
}

/// <summary>The detail a detail view shows, as SOLIDWORKS holds it.</summary>
public sealed class DetailFacts
{
    public string Label { get; set; } = "";

    /// <summary>Its detail circle's name in its parent (IDetailCircle.GetName).</summary>
    public string Circle { get; set; } = "";

    /// <summary>
    /// Where its parent draws its circle's centre, sheet mm (the circle's profile arc in the parent's own sketch, placed
    /// with ViewCutGeometry.FromViewSketch), or null when SOLIDWORKS gives no arc.
    /// </summary>
    public SheetPoint? Centre { get; set; }
}

/// <summary>A model entity a view shows: the view's model's own object, and whether it is a silhouette edge or a vertex.</summary>
public sealed class VisibleEntity
{
    public VisibleEntity(object entity, bool vertex, bool silhouette)
    {
        Entity = entity;
        Vertex = vertex;
        Silhouette = silhouette;
    }

    public object Entity { get; }

    public bool Vertex { get; }

    public bool Silhouette { get; }
}

/// <summary>
/// The document settings of a drawing the agent creates (design 611, Session preferences), by the names the add-in gives
/// them: each is set to <see cref="Value"/> on the agent's own new drawing, never on a drawing the user opened.
/// </summary>
public static class DrawingSettings
{
    /// <summary>What each is set to: false for all, so the drawing shows the part, not its scaffolding, and a view is made with nothing inserted.</summary>
    public const bool Value = false;

    public static readonly IReadOnlyList<string> All = new[]
    {
        "display_origins", "display_planes", "display_axes", "display_temporary_axes", "display_sketches", "display_reference_points",
        "auto_insert_center_marks_holes", "auto_insert_center_marks_fillets", "auto_insert_center_marks_slots",
        "auto_insert_center_lines", "auto_insert_balloons", "auto_insert_dims_marked_for_drawing",
    };
}

/// <summary>An annotation as SOLIDWORKS holds it. Positions in sheet mm; a dimension's value in model mm or degrees.</summary>
public sealed class AnnotationFacts
{
    /// <summary>Its name (IAnnotation.GetName), unique in its view or on its sheet.</summary>
    public string Name { get; set; } = "";

    public string Sheet { get; set; } = "";

    /// <summary>The view it belongs to; null for one on the sheet itself.</summary>
    public string? View { get; set; }

    public AnnotationKind Kind { get; set; }

    public SheetPoint? Position { get; set; }

    /// <summary>
    /// How <see cref="Position"/> was read, when more than one reading was taken (a centre mark: IAnnotation.GetPosition
    /// and ICenterMark.GetPosition), so a failed position check says what SOLIDWORKS gave each way.
    /// </summary>
    public string? PositionNote { get; set; }

    /// <summary>A note's text as shown (INote.GetText), linked properties resolved.</summary>
    public string? Text { get; set; }

    /// <summary>A note's text as written (INote.PropertyLinkedText).</summary>
    public string? LinkedText { get; set; }

    public DimensionKind? DimensionKind { get; set; }

    public double? Value { get; set; }

    /// <summary>A dimension's full name as SOLIDWORKS gives it (IDimension.FullName): D1@Sketch2@block.Part, RD1@Drawing View1.</summary>
    public string? DimensionName { get; set; }

    /// <summary>True for a driven (reference) dimension (IDimension.DrivenState).</summary>
    public bool Driven { get; set; }

    /// <summary>A datum tag's label.</summary>
    public string? Label { get; set; }

    public GtolFrame? Gtol { get; set; }

    /// <summary>The model entities it is attached to, as SOLIDWORKS gives them.</summary>
    public IReadOnlyList<object> Attached { get; set; } = new List<object>();

    public AnnotationFacts Copy() => new AnnotationFacts
    {
        Name = Name, Sheet = Sheet, View = View, Kind = Kind, Position = Position == null ? null : new SheetPoint(Position.X, Position.Y), PositionNote = PositionNote, Text = Text, LinkedText = LinkedText,
        DimensionKind = DimensionKind, Value = Value, DimensionName = DimensionName, Driven = Driven, Label = Label,
        Gtol = Gtol == null ? null : new GtolFrame { Characteristic = Gtol.Characteristic, ToleranceMm = Gtol.ToleranceMm, Diameter = Gtol.Diameter, MaterialCondition = Gtol.MaterialCondition, Datums = new List<string>(Gtol.Datums) },
        Attached = new List<object>(Attached),
    };
}

/// <summary>A custom property of a drawing: its value as written and as SOLIDWORKS resolves it.</summary>
public sealed class PropertyFacts
{
    public PropertyFacts(string name, string value, string resolved)
    {
        Name = name;
        Value = value;
        Resolved = resolved;
    }

    public string Name { get; }

    public string Value { get; }

    public string Resolved { get; }
}
