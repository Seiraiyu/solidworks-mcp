using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("drawing.read", typeof(DrawingReadResult), ReadOnly = true)]
[Description("Reads a drawing as a drafter reads it: every sheet (name, reference, size in sheet mm and the standard size it is, scale, projection, sheet format) and every view on it (reference, name, type, model, configuration, orientation, parent, centre and drawn extents in sheet mm, scale, display, exploded, annotation and table counts), the drawing's unsaved-changes flag, and each model it shows with its own. Sheets and views are named by reference (r1.sheet.…, r1.view.…) or by name (Sheet1, Drawing View1). Changes nothing.")]
public sealed class DrawingReadRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A model a drawing shows.")]
public sealed class DrawingModel
{
    [Required]
    [Description("Full path of the model's file, as the drawing names it.")]
    public string Path { get; set; } = "";

    [Description("True when it is loaded in SOLIDWORKS.")]
    public bool Loaded { get; set; }

    [Description("True when it has unsaved changes (document.save of the drawing saves it first).")]
    public bool Dirty { get; set; }

    [Description("True when its file is not on disk: views of it draw nothing.")]
    public bool Missing { get; set; }
}

[Description("A drawing, read.")]
public sealed class DrawingReadResult
{
    [Required]
    [Description("The drawing; dirty is its own unsaved-changes flag.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("The sheet shown now.")]
    public string? ActiveSheet { get; set; }

    [Required]
    [Description("Every sheet, in order, each with its views.")]
    public List<SheetInfo> Sheets { get; set; } = new List<SheetInfo>();

    [Required]
    [Description("Each model the drawing shows.")]
    public List<DrawingModel> Models { get; set; } = new List<DrawingModel>();
}
