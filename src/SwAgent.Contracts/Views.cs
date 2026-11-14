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
