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

    /// <summary>Deletes views by selecting each (SelectByID2 "DRAWINGVIEW", appended) and DeleteSelection2; false when SOLIDWORKS refused.</summary>
    bool DeleteViews(IReadOnlyList<string> views);

    /// <summary>The document of the model a view shows (IView.ReferencedDocument); null when it shows none or it is not loaded.</summary>
    ISwDocument? ModelOf(string view);

    /// <summary>
    /// The model's edges, silhouette edges and vertices a view shows (IView.GetVisibleEntities2 with no component), as the
    /// view's model's own objects, each once.
    /// </summary>
    IReadOnlyList<VisibleEntity> VisibleEntities(string view, bool edges, bool vertices);

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

    /// <summary>Full path of its sheet format (.SLDDRT), or null when it has none.</summary>
    public string? Format { get; set; }

    public SheetFacts Copy() => new SheetFacts
    {
        Name = Name, WidthMm = WidthMm, HeightMm = HeightMm, Scale = new ScaleRatio(Scale.Numerator, Scale.Denominator), Projection = Projection, Format = Format,
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
