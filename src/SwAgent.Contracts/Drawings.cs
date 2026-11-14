using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("How a sheet lays out projected views (its projection type).")]
public enum Projection
{
    [Description("First-angle projection (ISO in Europe): the view from the right is drawn on the left of the view it was projected from, the view from above below it.")]
    FirstAngle,

    [Description("Third-angle projection (ANSI): the view from the right is drawn on the right, the view from above above it.")]
    ThirdAngle,
}

[Description("A standard paper size: ISO a4 to a0, ANSI a to e.")]
public enum SheetSize
{
    A4,
    A3,
    A2,
    A1,
    A0,
    A,
    B,
    C,
    D,
    E,
}

[Description("Which way a standard sheet lies.")]
public enum SheetOrientation
{
    [Description("Wider than tall.")]
    Landscape,

    [Description("Taller than wide.")]
    Portrait,
}

[Description("What kind of drawing view it is.")]
public enum ViewType
{
    [Description("A view of a model in one orientation (front, isometric, a named view).")]
    Model,

    [Description("A view projected from another view, aligned with it.")]
    Projected,

    [Description("A section view.")]
    Section,

    [Description("A detail view.")]
    Detail,

    [Description("A sheet-metal part's flat pattern.")]
    FlatPattern,

    [Description("Any other kind (auxiliary, relative, crop, broken-out): read only.")]
    Other,
}

[Description("How a drawing view draws its model.")]
public enum ViewDisplay
{
    [Description("Visible edges solid, hidden edges dashed.")]
    HiddenLinesVisible,

    [Description("Visible edges only.")]
    HiddenLinesRemoved,

    [Description("Every edge, solid.")]
    Wireframe,

    [Description("Shaded faces, no edges.")]
    Shaded,

    [Description("Shaded faces with visible edges.")]
    ShadedWithEdges,
}

[Description("Which side of a view's model a projected view shows it from.")]
public enum ProjectDirection
{
    [Description("Seen from the left.")]
    Left,

    [Description("Seen from the right.")]
    Right,

    [Description("Seen from above.")]
    Above,

    [Description("Seen from below.")]
    Below,
}

[Description("A position on a drawing sheet, in millimetres from the sheet's lower-left corner: x to the right, y up. Sheet millimetres, not the model's.")]
public sealed class SheetPoint
{
    public SheetPoint()
    {
    }

    public SheetPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    [Description("Mm to the right of the sheet's lower-left corner.")]
    public double X { get; set; }

    [Description("Mm above the sheet's lower-left corner.")]
    public double Y { get; set; }

    public override string ToString() => System.FormattableString.Invariant($"({X:0.###}, {Y:0.###})");
}

[Description("A rectangle on a sheet, sheet millimetres from its lower-left corner.")]
public sealed class SheetBox
{
    [Required]
    [Description("The lower-left corner.")]
    public SheetPoint Min { get; set; } = new SheetPoint();

    [Required]
    [Description("The upper-right corner.")]
    public SheetPoint Max { get; set; } = new SheetPoint();

    public override string ToString() => $"{Min}..{Max}";
}

[Description("A drawing scale as a ratio: 1:2 is numerator 1, denominator 2 (the view is drawn at half size).")]
public sealed class ScaleRatio
{
    public ScaleRatio()
    {
    }

    public ScaleRatio(double numerator, double denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    [Description("The first number of the ratio, more than 0.")]
    public double Numerator { get; set; } = 1;

    [Description("The second number of the ratio, more than 0.")]
    public double Denominator { get; set; } = 1;

    public override string ToString() => System.FormattableString.Invariant($"{Numerator:0.####}:{Denominator:0.####}");
}

[Description("An entity of a model as one drawing view shows it: the view, and the entity in exactly one of the forms the model tools take (a reference issued in the view's model, a {component, entity} pair, or a name such as Boss-Extrude1 or Axis1@pin-1). It is resolved in the view's model and selected through that view.")]
public sealed class ViewEntity
{
    [Required]
    [Description("The view: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("A reference issued in the view's model (model.find, view.entities), alone. One of reference or entity.")]
    public string? Reference { get; set; }

    [Description("With entity in an assembly view: the component, by reference or name; entity is then a reference issued in the component's own part, or a name there.")]
    public string? Component { get; set; }

    [Description("A name in the view's model (Boss-Extrude1, front, Axis1@pin-1), or with component an entity of that component. One of reference or entity.")]
    public string? Entity { get; set; }
}

[Description("The sheet to make or change: a standard size, or a custom width and height, and its scale, projection and sheet format. Absent values are left as the template or the sheet has them.")]
public sealed class SheetSetup
{
    [Description("A standard paper size: a4, a3, a2, a1, a0 (ISO) or a, b, c, d, e (ANSI). Not with width_mm and height_mm.")]
    public SheetSize? Size { get; set; }

    [Description("With size: landscape (the default) or portrait.")]
    public SheetOrientation? Orientation { get; set; }

    [Description("A custom sheet's width, sheet mm, with height_mm; 10 to 10000.")]
    public double? WidthMm { get; set; }

    [Description("A custom sheet's height, sheet mm, with width_mm; 10 to 10000.")]
    public double? HeightMm { get; set; }

    [Description("The sheet's scale, which every view that follows the sheet's scale is drawn at.")]
    public ScaleRatio? Scale { get; set; }

    [Description("first_angle or third_angle.")]
    public Projection? Projection { get; set; }

    [Description("Full path of a sheet format (.SLDDRT) to draw the sheet in. The sheet's own (for a new sheet, the first sheet's) when absent.")]
    public string? Format { get; set; }
}

[Description("A drawing view, as drawing.read and the view operations report it. Positions and sizes in sheet mm.")]
public sealed class ViewInfo
{
    [Required]
    [Description("Its name, for example Drawing View1.")]
    public string Name { get; set; } = "";

    [Description("Its reference (r1.view.…). It names the view by its name: a view renamed or deleted no longer resolves. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Required]
    [Description("The sheet it is on.")]
    public string Sheet { get; set; } = "";

    [Description("model, projected, section, detail, flat_pattern or other.")]
    public ViewType Type { get; set; }

    [Description("Full path of the model it shows; absent when it shows none.")]
    public string? Model { get; set; }

    [Description("The model's configuration it shows.")]
    public string? Configuration { get; set; }

    [Description("Its orientation: front, back, left, right, top, bottom, isometric, dimetric, trimetric, or the model's named view; absent for a view whose orientation follows another (projected) unless SOLIDWORKS names one.")]
    public string? Orientation { get; set; }

    [Description("The view it was projected (or cut, or detailed) from; absent for a model view.")]
    public string? Parent { get; set; }

    [Required]
    [Description("Its centre on the sheet; for a detail, the centre of what its circle shows (where the detail draws the circle's centre), as view.detail and view.set take it.")]
    public SheetPoint Position { get; set; } = new SheetPoint();

    [Required]
    [Description("The scale it is drawn at.")]
    public ScaleRatio Scale { get; set; } = new ScaleRatio();

    [Description("True when it follows the sheet's scale.")]
    public bool SheetScale { get; set; }

    [Description("How it draws its model; absent when SOLIDWORKS reports a mode the add-in does not name.")]
    public ViewDisplay? Display { get; set; }

    [Description("True when it shows its assembly exploded.")]
    public bool Exploded { get; set; }

    [Description("The box around what it draws (its edges as drawn, not its border), sheet mm; absent when it draws nothing.")]
    public SheetBox? Extents { get; set; }

    [Description("How many annotations it holds.")]
    public int Annotations { get; set; }

    [Description("How many tables are anchored to it.")]
    public int Tables { get; set; }
}

[Description("A sheet of a drawing. Sizes in sheet mm.")]
public sealed class SheetInfo
{
    [Required]
    [Description("Its name, for example Sheet1.")]
    public string Name { get; set; } = "";

    [Description("Its reference (r1.sheet.…). It names the sheet by its name: a sheet renamed or deleted no longer resolves. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("Its standard size when its width and height are one's (within 0.01 mm); absent for a custom size.")]
    public SheetSize? Size { get; set; }

    [Description("landscape or portrait.")]
    public SheetOrientation Orientation { get; set; }

    [Description("Its width, sheet mm.")]
    public double WidthMm { get; set; }

    [Description("Its height, sheet mm.")]
    public double HeightMm { get; set; }

    [Required]
    [Description("Its scale.")]
    public ScaleRatio Scale { get; set; } = new ScaleRatio();

    [Description("first_angle or third_angle.")]
    public Projection Projection { get; set; }

    [Description("Its sheet format: the full path of its .SLDDRT, or *.drt for the drawing template's own format (held in the template, with no file); absent when it has none.")]
    public string? Format { get; set; }

    [Description("True for the sheet shown now.")]
    public bool Active { get; set; }

    [Required]
    [Description("Its views, in SOLIDWORKS' order.")]
    public List<ViewInfo> Views { get; set; } = new List<ViewInfo>();
}

[Description("A model a drawing shows, as it is in memory.")]
public sealed class DrawingModelState
{
    [Required]
    [Description("Full path of the model.")]
    public string Path { get; set; } = "";

    [Description("True when it had unsaved changes before the change.")]
    public bool DirtyBefore { get; set; }

    [Description("True when it has unsaved changes after the change.")]
    public bool DirtyAfter { get; set; }

    [Description("How many of its features had a rebuild error before the change.")]
    public int RebuildErrorsBefore { get; set; }

    [Description("How many of its features have a rebuild error after the change.")]
    public int RebuildErrorsAfter { get; set; }
}

/// <summary>A result that carries the evidence block every drawing-changing operation returns.</summary>
public interface IDrawingChangeResult
{
    DrawingChange Change { get; set; }
}

[Description("The evidence every drawing-changing operation returns (design 611, Drawing changes): what it made and edited, the drawing's sheets and views before and after, and each model the drawing shows, which a drawing change never changes.")]
public sealed class DrawingChange
{
    [Required]
    [Description("The drawing it acted on.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("What it made (sheets, views), in the order it made them.")]
    public List<CreatedItem> Created { get; set; } = new List<CreatedItem>();

    [Required]
    [Description("What it edited, each with its value before and after.")]
    public List<ChangedItem> Changed { get; set; } = new List<ChangedItem>();

    [Description("Sheets before the change.")]
    public int SheetsBefore { get; set; }

    [Description("Sheets after the change.")]
    public int SheetsAfter { get; set; }

    [Description("Views before the change, on every sheet.")]
    public int ViewsBefore { get; set; }

    [Description("Views after the change, on every sheet.")]
    public int ViewsAfter { get; set; }

    [Required]
    [Description("Each model the drawing shows, before and after: unsaved changes and rebuild errors, which must not have changed.")]
    public List<DrawingModelState> Models { get; set; } = new List<DrawingModelState>();
}
