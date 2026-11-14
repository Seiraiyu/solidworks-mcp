using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("Which way a section line crosses its parent view on the sheet.")]
public enum CutOrientation
{
    [Description("Straight across, left to right: the section view looks up or down the sheet at the cut and is drawn above or below the parent, sharing its vertical centre line.")]
    Horizontal,

    [Description("Straight up and down: the section view looks across the sheet at the cut and is drawn beside the parent, sharing its horizontal centre line.")]
    Vertical,
}

[Description("A place in the model a view shows, where a section line passes or a detail circle is centred: a point in model mm, or an entity of the model whose position is taken (a vertex; a straight edge's midpoint; a circular edge's centre; an axis, a reference point, or a cylindrical face's axis), in exactly one of the forms the model tools take (point, a reference issued in the view's model, or a name such as Axis1, with component for an entity of a component in an assembly view). It is resolved in the view's model, never substituted.")]
public sealed class ModelPlace
{
    [Description("A point in the view's model, model mm (the model's own coordinates, not the sheet's). One of point, reference or entity.")]
    public Point3? Point { get; set; }

    [Description("A reference issued in the view's model (model.find, view.entities), alone. One of point, reference or entity.")]
    public string? Reference { get; set; }

    [Description("With entity in an assembly view: the component, by reference or name; entity is then a reference issued in the component's own part, or a name there.")]
    public string? Component { get; set; }

    [Description("A name in the view's model (Axis1, Point1, or with component an entity of that component). One of point, reference or entity.")]
    public string? Entity { get; set; }
}

[Description("Where a section line crosses its parent view: through a place in the parent's model, straight across the sheet or straight up and down it.")]
public sealed class SectionCut
{
    [Required]
    [Description("The place the line passes through: a point in the parent's model (model mm) or an entity whose position is taken (a hole's axis or circular edge, a vertex, a straight edge's midpoint).")]
    public ModelPlace Through { get; set; } = new ModelPlace();

    [Description("horizontal (straight across the sheet) or vertical (straight up and down it).")]
    public CutOrientation Orientation { get; set; }
}

[Operation("view.section", typeof(ViewSectionResult))]
[Description("Cuts a full section view from a model or projected view (IDrawingDoc.CreateSectionViewAt5). The add-in draws the section line itself in the parent view's sketch, horizontal or vertical on the sheet through cut.through (a point in the parent's model, model mm, or an entity whose position is taken: a hole's axis or circular edge, a vertex, a straight edge's midpoint), from 5 mm before what the parent draws to 5 mm after it; a place drawn on or outside the parent's drawn edges is refused, since that line would cut nothing. The section is aligned with its parent (SOLIDWORKS aligns section views): below it for a horizontal line and to its right for a vertical one, 20 mm from its drawn edges, when at is absent; at (sheet mm) must be on the parent's vertical centre line (its x) for a horizontal line, its horizontal one (its y) for a vertical line. label is the next free letter (A, B, …, without I, O and Q) when absent; reversed looks the other way. Checked by measuring: a section view of the parent with that label and direction, at the parent's scale, centred along its free axis where asked within 0.01 mm, facing along the parent's up (horizontal) or right (vertical) and sharing its centre line (from both views' transforms); the faces it hatches lie in the plane through cut.through within 0.01 mm (a view that hatches nothing cut nothing and is refused); what it draws lies within the model's box projected through its transform; and the model is unchanged. Otherwise the view and its section line are deleted again (rolled back).")]
public sealed class ViewSectionRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view to cut: a reference (r1.view.…) or its name (Drawing View1). A model or projected view.")]
    public string Parent { get; set; } = "";

    [Required]
    [Description("Where the section line crosses the parent.")]
    public SectionCut Cut { get; set; } = new SectionCut();

    [Description("The section's label (letters and digits, up to 4): A-A is labelled A. The next free letter when absent; one another section or detail of the drawing has is refused.")]
    public string? Label { get; set; }

    [Description("Where the section view's centre goes, sheet mm, on the parent's centre line. Beside the parent when absent.")]
    public SheetPoint? At { get; set; }

    [Description("True to look the other way along the cut (SOLIDWORKS' flip direction).")]
    public bool? Reversed { get; set; }
}

[Description("The section view made.")]
public sealed class ViewSectionResult : IDrawingChangeResult
{
    [Required]
    [Description("The section view, as read back.")]
    public ViewInfo View { get; set; } = new ViewInfo();

    [Required]
    [Description("Its label, as read back.")]
    public string Label { get; set; } = "";

    [Description("True when it looks the other way from SOLIDWORKS' default, as read back.")]
    public bool Reversed { get; set; }

    [Required]
    [Description("The point the cut passes through, model mm.")]
    public Point3 Through { get; set; } = new Point3();

    [Required]
    [Description("What cut.through named, as resolved: the point, or the entity and its position.")]
    public string ThroughIs { get; set; } = "";

    [Description("How many faces it hatches (each measured in the cutting plane).")]
    public int HatchedFaces { get; set; }

    [Description("The area of the faces it hatches, mm²: the model's cross-section in the cutting plane.")]
    public double HatchedAreaMm2 { get; set; }

    [Description("True when its model has unsaved changes: the section shows the model as it is in memory.")]
    public bool ModelUnsaved { get; set; }

    [Required]
    [Description("What changed in the drawing: created holds the section view; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}
