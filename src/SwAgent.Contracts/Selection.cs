using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("selection.get", typeof(SelectionResult), ReadOnly = true)]
[Description("What is selected now in a part or an assembly, each item with its reference and shape: how an agent acts on what the user picked in SOLIDWORKS (\"fillet these edges\", \"mate these faces\"). In an assembly a selected face, edge or vertex names its component and is described in the assembly's coordinates, and a selected component is a component item. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class SelectionGetRequest
{
    [Description("The part or assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part's or assembly's selection.")]
public sealed class SelectionResult
{
    [Required]
    [Description("The part or assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("How many items are selected.")]
    public int Total { get; set; }

    [Required]
    [Description("The selected items, in the order they were selected; the first 1000 when more are selected (see total).")]
    public List<SelectedItem> Items { get; set; } = new List<SelectedItem>();
}

[Description("One selected item.")]
public sealed class SelectedItem
{
    [Required]
    [Description("What SOLIDWORKS says it is: face, edge, vertex, plane, axis, point, sketch, sketch_segment, sketch_point, dimension, feature, solid_body, component or other.")]
    public string Type { get; set; } = "";

    [Description("It, with its reference; absent when it is nothing the add-in can name (an annotation, a temporary axis).")]
    public EntityInfo? Entity { get; set; }
}

[Operation("diag.select", typeof(SelectionResult), Internal = true)]
[Description("Fault testing only: selects entities as the user would, so selection.changed is raised as for the user's own selection. Refused unless diagnostics are on.")]
public sealed class DiagSelectRequest
{
    [Description("The part or assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("References exactly as results gave them, or names (front, top, right, origin, a feature's name; in an assembly, a component's name@component or a component's own name), to select in order.")]
    public List<string> Items { get; set; } = new List<string>();

    [Description("Add to the selection instead of replacing it. Default false.")]
    public bool? Append { get; set; }
}

[Event("selection.changed")]
[Description("The user's selection changed in a part or an assembly (raised once it has been still for 250 ms; never for the add-in's own selections).")]
public sealed class SelectionChangedEvent
{
    [Required]
    [Description("The part's or assembly's title.")]
    public string Title { get; set; } = "";

    [Description("Its full path, absent while unsaved.")]
    public string? Path { get; set; }

    [Description("How many items are selected now.")]
    public int Total { get; set; }

    [Required]
    [Description("What is selected now, the first 100 when more are (see total; selection.get reads more); empty when the selection was cleared.")]
    public List<SelectedItem> Items { get; set; } = new List<SelectedItem>();
}
