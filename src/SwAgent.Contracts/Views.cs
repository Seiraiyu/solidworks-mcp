using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("view.create", typeof(ViewResult))]
[Description("Places a model view of a saved part or assembly on a drawing sheet (IDrawingDoc.CreateDrawViewFromModelView3): front, back, left, right, top, bottom, isometric, dimetric, trimetric, or a named view the model has, centred at a point in sheet mm (from the sheet's lower-left corner), at the sheet's scale or its own. The model must be a saved file: a never-saved model is refused before SOLIDWORKS is called (save it with sw_document_save_as first); a model with unsaved changes is drawn as it is in memory (model_unsaved says so). Checked by measuring: the view is on the sheet with the model, configuration and display asked; its centre reads back within 0.01 mm and its scale exactly; it faces the orientation asked (the model axis toward the reader from its model-to-sheet transform: +Z front, -Z back, +Y top, -Y bottom, +X right, -X left; equal components for isometric); what it draws (its edges) equals the model's bounding box projected and scaled within 0.01 mm for the six orthographic views, and lies inside it for the others; and the model is unchanged. Otherwise the view is deleted again (rolled back).")]
public sealed class ViewCreateRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The sheet to put it on: a reference (r1.sheet.…) or a name. The sheet shown when absent; another is shown first.")]
    public string? Sheet { get; set; }

    [Required]
    [Description("The model: the full path of a saved .SLDPRT or .SLDASM, or the title of an open part or assembly that has been saved.")]
    public string Model { get; set; } = "";

    [Description("The model's configuration to show; the model's active one when absent.")]
    public string? Configuration { get; set; }

    [Required]
    [Description("front, back, left, right, top, bottom, isometric, dimetric, trimetric, or the name of a view the model has.")]
    public string Orientation { get; set; } = "";

    [Required]
    [Description("Where its centre goes on the sheet, sheet mm from the lower-left corner.")]
    public SheetPoint At { get; set; } = new SheetPoint();

    [Description("Its own scale (1:2 draws at half size); it follows the sheet's scale when absent.")]
    public ScaleRatio? Scale { get; set; }

    [Description("hidden_lines_visible, hidden_lines_removed, wireframe, shaded or shaded_with_edges. Hidden lines visible for the six orthographic views of a part, hidden lines removed otherwise, when absent.")]
    public ViewDisplay? Display { get; set; }
}

[Operation("view.project", typeof(ViewResult))]
[Description("Projects a view from another (IDrawingDoc.CreateUnfoldedViewAt3 with the parent selected): the parent's model seen from its left, right, above or below, drawn on the side the sheet's projection implies (third angle: the view from the right on the right; first angle: on the left) and aligned with the parent, as SOLIDWORKS aligns projected views. at (sheet mm) must be on that side and on the parent's centre line (its y for left and right, its x for above and below); beside the parent with a 20 mm gap when absent. Checked by measuring: a projected view of the parent, at the parent's scale, centred where asked within 0.01 mm, turned 90° from the parent toward that side and sharing its centre line (from both views' transforms), drawing the model's box projected and scaled within 0.01 mm; and the model is unchanged. Otherwise the view is deleted again (rolled back).")]
public sealed class ViewProjectRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view to project from: a reference (r1.view.…) or its name (Drawing View1).")]
    public string Parent { get; set; } = "";

    [Description("Which side of the model the new view shows: left, right, above or below.")]
    public ProjectDirection Direction { get; set; }

    [Description("Where its centre goes, sheet mm: on the side the sheet's projection implies, on the parent's centre line. Beside the parent when absent.")]
    public SheetPoint? At { get; set; }
}

[Operation("view.set", typeof(ViewResult))]
[Description("Moves, rescales, restyles or reconfigures a drawing view in place: at (its centre, sheet mm), scale (its own) or sheet_scale (follow the sheet's), display, configuration (a model view's). Values not given stay; moved without at, a view keeps its centre. A projected or section view is drawn at its parent's scale and moves only along its parent's centre line (SOLIDWORKS silently keeps the aligned coordinate): moving it off that line is refused naming the parent. Each value is recorded before and put back on failure. Checked by measuring: every value reads back (the centre within 0.01 mm, the scale exactly), what it draws equals the model's box projected at its scale within 0.01 mm (orthographic model and projected views; inside it for others; a section, detail or flat pattern draws what it drew, moved and rescaled about its centre), the views projected or cut from it are still on its centre lines, and the model is unchanged.")]
public sealed class ViewSetRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("Where its centre goes, sheet mm from the sheet's lower-left corner.")]
    public SheetPoint? At { get; set; }

    [Description("Its own scale (a model view's). Not with sheet_scale.")]
    public ScaleRatio? Scale { get; set; }

    [Description("true to make it follow its sheet's scale. Not with scale.")]
    public bool? SheetScale { get; set; }

    [Description("hidden_lines_visible, hidden_lines_removed, wireframe, shaded or shaded_with_edges.")]
    public ViewDisplay? Display { get; set; }

    [Description("The model's configuration a model view shows.")]
    public string? Configuration { get; set; }
}

[Operation("view.delete", typeof(ViewDeleteResult))]
[Description("Deletes drawing views by selecting each (SelectByID2 \"DRAWINGVIEW\") and deleting the selection. A view other views were made from (projected, section, detail) is refused unless they are deleted in the same request. A section or detail view takes its section line or detail circle with it from a parent that is kept. Checked: each view is gone (with its section line or detail circle) and every other view is still there. Not rolled back (a deleted view's annotations cannot be made again as they were): a failed check says what is left. A reference to a deleted view no longer resolves.")]
public sealed class ViewDeleteRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The views to delete: each a reference (r1.view.…) or a name (Drawing View2).")]
    public List<string> Views { get; set; } = new List<string>();
}

[Description("The views deleted.")]
public sealed class ViewDeleteResult : IDrawingChangeResult
{
    [Required]
    [Description("The names of the views deleted.")]
    public List<string> Deleted { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the drawing: views before and after.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Description("What view.entities lists.")]
public enum ViewEntityKind
{
    [Description("Edges and silhouette edges the view draws.")]
    Edge,

    [Description("Vertices the view shows.")]
    Vertex,
}

[Description("How an entity is drawn on the sheet.")]
public enum DrawnShape
{
    [Description("A straight line: start and end.")]
    Line,

    [Description("A whole circle seen face on: centre and radius.")]
    Circle,

    [Description("Part of a circle seen face on: centre and radius.")]
    Arc,

    [Description("A point: a vertex, or a line seen end on.")]
    Point,

    [Description("Anything else (a circle seen at an angle, a spline): no sheet geometry is given.")]
    Other,
}

[Operation("view.entities", typeof(ViewEntitiesResult), ReadOnly = true)]
[Description("Lists what a view of a part shows (IView.GetVisibleEntities2): each visible edge, silhouette edge and vertex of the model, with its model reference (r1.edge.…, r1.vertex.…, issued in the part, when SOLIDWORKS gives the part a persistent id for it), its description in model mm, and how it is drawn on the sheet in sheet mm: a line's start and end, a circle's or arc's centre and radius (a circle seen face on), a vertex's point, computed from the model's geometry through the view's model-to-sheet transform. This is how to find the edge to dimension or the hole to tolerance, and to check that a reference already held is visible in a view. Views of parts only: an assembly view is refused. Changes nothing.")]
public sealed class ViewEntitiesRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("edge, vertex or both. Both when absent.")]
    public List<ViewEntityKind>? Kinds { get; set; }
}

[Description("An entity of the model as a view shows it.")]
public sealed class ViewEntityInfo
{
    [Description("Its model reference, issued in the part; absent when SOLIDWORKS gives none for it. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("edge or vertex.")]
    public ReferenceKind Kind { get; set; }

    [Description("True for a silhouette edge (the outline of a curved face), which has no edge of its own in the model.")]
    public bool Silhouette { get; set; }

    [Description("How it is drawn: line, circle, arc, point or other.")]
    public DrawnShape Shape { get; set; }

    [Description("A line's start, or a point's position, sheet mm.")]
    public SheetPoint? Start { get; set; }

    [Description("A line's end, sheet mm.")]
    public SheetPoint? End { get; set; }

    [Description("A circle's or arc's centre, sheet mm.")]
    public SheetPoint? Centre { get; set; }

    [Description("A circle's or arc's radius as drawn, sheet mm (the model's radius times the view's scale).")]
    public double? RadiusMm { get; set; }

    [Description("The entity in the model: its shape and measurements in model mm.")]
    public EntityInfo? Entity { get; set; }
}

[Description("What a view shows.")]
public sealed class ViewEntitiesResult
{
    [Required]
    [Description("The view.")]
    public string View { get; set; } = "";

    [Required]
    [Description("Full path of its model.")]
    public string Model { get; set; } = "";

    [Description("The view's scale as a number (0.5 for 1:2).")]
    public double Scale { get; set; }

    [Required]
    [Description("Each entity it shows: edges first, then vertices.")]
    public List<ViewEntityInfo> Entities { get; set; } = new List<ViewEntityInfo>();
}

[Description("The view made or changed.")]
public sealed class ViewResult : IDrawingChangeResult
{
    [Required]
    [Description("The view, as read back.")]
    public ViewInfo View { get; set; } = new ViewInfo();

    [Description("True when its model has unsaved changes: the view draws the model as it is in memory, not as its file is (document.save of the drawing saves the model first).")]
    public bool ModelUnsaved { get; set; }

    [Required]
    [Description("What changed in the drawing: created holds a new view; changed each value set, before and after; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}
