using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("Whether a component's model is loaded, and how.")]
public enum ComponentState
{
    [Description("Loaded in full: its geometry can be referenced.")]
    Resolved,

    [Description("Loaded lightweight: its geometry cannot be referenced until it is resolved.")]
    Lightweight,

    [Description("Suppressed in the active configuration: not loaded, not shown, not solved.")]
    Suppressed,

    [Description("Its file was not found when the assembly opened.")]
    Missing,
}

[Description("What component.set makes a component: suppressed, or resolved (loaded in full).")]
public enum Suppression
{
    [Description("Loaded in full.")]
    Resolved,

    [Description("Suppressed in the active configuration.")]
    Suppressed,
}

[Description("How a subassembly solves inside its parent.")]
public enum SolveAs
{
    [Description("As one rigid body: its own components do not move.")]
    Rigid,

    [Description("Flexibly: its own components move as their mates allow.")]
    Flexible,
}

[Description("How far a component's position is defined (IComponent2.GetConstrainedStatus).")]
public enum Constraint
{
    [Description("Fixed in place.")]
    Fixed,

    [Description("Fully defined by its mates: it cannot move.")]
    FullyDefined,

    [Description("Under defined: it can still move.")]
    UnderDefined,

    [Description("Over defined: its mates conflict.")]
    OverDefined,

    [Description("SOLIDWORKS could not say (no solution, or solving is off).")]
    Unknown,
}

[Description("A component of an assembly. Positions are in the assembly's coordinates, mm.")]
public sealed class ComponentInfo
{
    [Required]
    [Description("Its name as SOLIDWORKS shows it, for example bracket-1; inside a subassembly the path of names, sub-1/bracket-1.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference (r1.component.…); absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("The full path of the file it is, absent for a component that has no file of its own.")]
    public string? Path { get; set; }

    [Description("The configuration of its file it uses.")]
    public string? Configuration { get; set; }

    [Description("Whether it is resolved, lightweight, suppressed or missing.")]
    public ComponentState State { get; set; }

    [Description("True when it is fixed in place.")]
    public bool Fixed { get; set; }

    [Description("True when it is hidden.")]
    public bool Hidden { get; set; }

    [Required]
    [Description("Where it sits in the assembly.")]
    public Transform Transform { get; set; } = new Transform();

    [Description("The box around it in the assembly, mm; absent when it is suppressed or has no geometry.")]
    public Box3? Box { get; set; }

    [Description("True when it is itself an assembly.")]
    public bool Subassembly { get; set; }

    [Description("For a subassembly, whether it solves rigid or flexible; absent for a part.")]
    public SolveAs? SolveAs { get; set; }

    [Description("How far its position is defined.")]
    public Constraint Constraint { get; set; }

    [Description("The subassembly it belongs to; absent at the top level.")]
    public string? Parent { get; set; }

    [Description("How many mates use it.")]
    public int Mates { get; set; }
}

/// <summary>A result that carries the evidence block every assembly-changing operation returns.</summary>
public interface IAssemblyChangeResult
{
    AssemblyChange Change { get; set; }
}

[Description("The evidence every assembly-changing operation returns (design 455, Assembly changes): what it made and edited, which components moved, and the assembly read after it.")]
public sealed class AssemblyChange
{
    [Required]
    [Description("The assembly it acted on.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("What it made (components, mates, patterns), in the order it made them.")]
    public List<CreatedItem> Created { get; set; } = new List<CreatedItem>();

    [Required]
    [Description("What it edited, each with its value before and after.")]
    public List<ChangedItem> Changed { get; set; } = new List<ChangedItem>();

    [Required]
    [Description("The assembly's rebuild errors and warnings after the change (mate errors included), by feature.")]
    public RebuildStatus Rebuild { get; set; } = new RebuildStatus();

    [Description("Components before the change, nested ones included.")]
    public int ComponentsBefore { get; set; }

    [Description("Components after the change, nested ones included.")]
    public int ComponentsAfter { get; set; }

    [Description("Mates before the change.")]
    public int MatesBefore { get; set; }

    [Description("Mates after the change.")]
    public int MatesAfter { get; set; }

    [Required]
    [Description("Every component whose position changed, the ones the request named and the ones a solve moved.")]
    public List<MovedComponent> Moved { get; set; } = new List<MovedComponent>();

    [Required]
    [Description("How far the position of each component the change made, edited or moved is defined now.")]
    public List<ComponentConstraint> Constraints { get; set; } = new List<ComponentConstraint>();
}

[Description("A component whose position a change moved.")]
public sealed class MovedComponent
{
    [Required]
    [Description("The component's name.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("Its origin before, mm in the assembly.")]
    public Point3 Before { get; set; } = new Point3();

    [Required]
    [Description("Its origin after, mm in the assembly.")]
    public Point3 After { get; set; } = new Point3();

    [Description("True when its axes turned as well as (or instead of) its origin moving.")]
    public bool Rotated { get; set; }
}

[Description("How far a component's position is defined.")]
public sealed class ComponentConstraint
{
    [Required]
    [Description("The component's name.")]
    public string Name { get; set; } = "";

    [Description("Fixed, fully defined, under defined, over defined or unknown.")]
    public Constraint Constraint { get; set; }
}
