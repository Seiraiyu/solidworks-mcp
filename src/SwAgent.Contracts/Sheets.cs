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
[Description("Adds a sheet to a drawing (IDrawingDoc.NewSheet4): a standard size (a4 … a0, a … e, landscape or portrait) or a custom width_mm and height_mm (sheet mm), a scale, a projection, and a sheet format (.SLDDRT), each the drawing's first sheet's when absent. The new sheet is shown. Checked: there is one more sheet, and its size reads back within 0.01 mm, its scale exactly, its projection and format as asked. Otherwise it is deleted again (rolled back)." + DrawingChangeText.Waits)]
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

[Operation("sheet.set", typeof(SheetResult))]
[Description("Changes a sheet in place: its name, size (a standard size or width_mm and height_mm, sheet mm), scale, projection or sheet format (.SLDDRT). Values not given stay. Through ISheet.SetProperties2, the scale through ISheet.SetScale. Checked: every value reads back (sizes within 0.01 mm, the scale exactly), and every view that follows the sheet's scale is drawn at the new scale (its transform's scale, and its drawn edges equal to its model's box projected at that scale). Otherwise the sheet's old values are put back (rolled back)." + DrawingChangeText.Waits)]
public sealed class SheetSetRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sheet: a reference (r1.sheet.…) or its name (Sheet1).")]
    public string Sheet { get; set; } = "";

    [Description("A new name for it; one no other sheet has.")]
    public string? Name { get; set; }

    [Description("A standard paper size: a4, a3, a2, a1, a0 (ISO) or a, b, c, d, e (ANSI). Not with width_mm and height_mm.")]
    public SheetSize? Size { get; set; }

    [Description("With size: landscape (the default) or portrait.")]
    public SheetOrientation? Orientation { get; set; }

    [Description("A custom width, sheet mm, with height_mm; 10 to 10000.")]
    public double? WidthMm { get; set; }

    [Description("A custom height, sheet mm, with width_mm; 10 to 10000.")]
    public double? HeightMm { get; set; }

    [Description("The sheet's scale: views that follow the sheet are redrawn at it.")]
    public ScaleRatio? Scale { get; set; }

    [Description("first_angle or third_angle.")]
    public Projection? Projection { get; set; }

    [Description("Full path of a sheet format (.SLDDRT) to draw the sheet in.")]
    public string? Format { get; set; }

    /// <summary>The size, scale, projection and format asked, as document.new's sheet takes them.</summary>
    public SheetSetup Setup() => new SheetSetup { Size = Size, Orientation = Orientation, WidthMm = WidthMm, HeightMm = HeightMm, Scale = Scale, Projection = Projection, Format = Format };
}

[Operation("sheet.activate", typeof(SheetActivateResult))]
[Description("Shows a sheet of a drawing (IDrawingDoc.ActivateSheet), so new views go on it. Changes what is shown only, not the drawing, so nothing is rolled back. Waits, like a change, while the user edits a sketch, the sheet format or has a command open. Checked: the sheet is the one shown.")]
public sealed class SheetActivateRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sheet: a reference (r1.sheet.…) or its name (Sheet1).")]
    public string Sheet { get; set; } = "";
}

[Description("The sheet shown now.")]
public sealed class SheetActivateResult
{
    [Required]
    [Description("The drawing.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The sheet shown now.")]
    public string Sheet { get; set; } = "";

    [Description("The sheet shown before.")]
    public string? Before { get; set; }
}

[Operation("sheet.delete", typeof(SheetDeleteResult))]
[Description("Deletes sheets of a drawing, and every view on them, by selecting each (SelectByID2 \"SHEET\") and deleting the selection. A drawing keeps at least one sheet: deleting its last is refused. Checked: each sheet is gone and every other sheet is still there. Not rolled back (a deleted sheet's views cannot be made again as they were): a failed check says what is left." + DrawingChangeText.Waits)]
public sealed class SheetDeleteRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The sheets to delete: each a reference (r1.sheet.…) or a name (Sheet2).")]
    public List<string> Sheets { get; set; } = new List<string>();
}

[Description("The sheets deleted.")]
public sealed class SheetDeleteResult : IDrawingChangeResult
{
    [Required]
    [Description("The names of the sheets deleted.")]
    public List<string> Deleted { get; set; } = new List<string>();

    [Description("How many views were on them, deleted with them.")]
    public int Views { get; set; }

    [Required]
    [Description("What changed in the drawing: sheets and views before and after.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
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
