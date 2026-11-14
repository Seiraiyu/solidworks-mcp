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
