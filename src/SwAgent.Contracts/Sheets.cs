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

[Operation("sheet.add", typeof(SheetResult))]
[Description("Adds a sheet to a drawing (IDrawingDoc.NewSheet4): a standard size (a4 … a0, a … e, landscape or portrait) or a custom width_mm and height_mm (sheet mm), a scale, a projection, and a sheet format (.SLDDRT), each the drawing's first sheet's when absent. The new sheet is shown. Checked: there is one more sheet, and its size reads back within 0.01 mm, its scale exactly, its projection and format as asked. Otherwise it is deleted again (rolled back).")]
public sealed class SheetAddRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The new sheet's name; one no sheet has. SOLIDWORKS' next free SheetN when absent.")]
    public string? Name { get; set; }

    [Description("A standard paper size: a4, a3, a2, a1, a0 (ISO) or a, b, c, d, e (ANSI). Not with width_mm and height_mm.")]
    public SheetSize? Size { get; set; }

    [Description("With size: landscape (the default) or portrait.")]
    public SheetOrientation? Orientation { get; set; }

    [Description("A custom sheet's width, sheet mm, with height_mm; 10 to 10000.")]
    public double? WidthMm { get; set; }

    [Description("A custom sheet's height, sheet mm, with width_mm; 10 to 10000.")]
    public double? HeightMm { get; set; }

    [Description("The sheet's scale: views that follow the sheet are drawn at it.")]
    public ScaleRatio? Scale { get; set; }

    [Description("first_angle or third_angle.")]
    public Projection? Projection { get; set; }

    [Description("Full path of a sheet format (.SLDDRT).")]
    public string? Format { get; set; }

    /// <summary>The size, scale, projection and format asked, as document.new's sheet takes them.</summary>
    public SheetSetup Setup() => new SheetSetup { Size = Size, Orientation = Orientation, WidthMm = WidthMm, HeightMm = HeightMm, Scale = Scale, Projection = Projection, Format = Format };
}

[Description("The sheet added or changed.")]
public sealed class SheetResult : IDrawingChangeResult
{
    [Required]
    [Description("The sheet, as read back, with its views.")]
    public SheetInfo Sheet { get; set; } = new SheetInfo();

    [Required]
    [Description("What changed in the drawing: created holds a new sheet; changed each value set, before and after.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
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
