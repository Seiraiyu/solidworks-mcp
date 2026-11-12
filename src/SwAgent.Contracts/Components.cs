using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("assembly.insert", typeof(AssemblyInsertResult))]
[Description("Inserts a part or assembly file into an assembly as a component, at a point (mm, assembly coordinates; the assembly origin by default) with its axes aligned to the assembly's unless x_axis and z_axis are given. The first component of an empty assembly is fixed unless fixed is false; later ones float unless fixed is true. Checked: one more component, of that file and configuration, where it was asked to be; otherwise it is removed again. Returns its reference (r1.component.…): opaque, pass it back exactly as given.")]
public sealed class AssemblyInsertRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("Full path of an existing .SLDPRT or .SLDASM file.")]
    public string Path { get; set; } = "";

    [Description("The configuration of the file to use; its active configuration when absent.")]
    public string? Configuration { get; set; }

    [Description("Where the component's origin goes, mm in the assembly. The assembly origin when absent.")]
    public Point3? At { get; set; }

    [Description("The component's x axis in the assembly, with z_axis (both or neither, perpendicular). The assembly's x axis when absent.")]
    public Vector3? XAxis { get; set; }

    [Description("The component's z axis in the assembly, with x_axis. The assembly's z axis when absent.")]
    public Vector3? ZAxis { get; set; }

    [Description("Fix it in place. Default: true for the first component of an empty assembly, false otherwise.")]
    public bool? Fixed { get; set; }
}

[Operation("assembly.components", typeof(AssemblyComponentsResult), ReadOnly = true)]
[Description("Lists an assembly's components in tree order, the top level only or the whole tree (each subassembly's components after it): name, reference, file, configuration, whether it is resolved, lightweight, suppressed or missing, fixed, hidden, where it sits (mm, assembly coordinates), its box, how a subassembly solves, and how far its position is defined. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class AssemblyComponentsRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("List the components inside subassemblies too. Default false: the top level only.")]
    public bool? Recursive { get; set; }
}

[Description("An assembly's components.")]
public sealed class AssemblyComponentsResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("Its components in tree order.")]
    public List<ComponentInfo> Components { get; set; } = new List<ComponentInfo>();
}

[Operation("component.place", typeof(ComponentPlaceResult))]
[Description("Moves a component to a point (mm, assembly coordinates), with new axes when x_axis and z_axis are given, so it sits near where it belongs before it is mated (a mate's closest alignment depends on where things are). Refuses a fixed component, one its mates leave no freedom (fully defined), and one inside a rigid subassembly. A component with mates is placed and then solved: solved says whether the solve moved it from where it was put. Checked: an unmated component reads back where it was put. Rolled back on failure.")]
public sealed class ComponentPlaceRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The component: its reference (r1.component.…) or its name (bracket-1; sub-1/pin-1 inside a subassembly).")]
    public string Component { get; set; } = "";

    [Required]
    [Description("Where the component's origin goes, mm in the assembly.")]
    public Point3 Origin { get; set; } = new Point3();

    [Description("The component's x axis in the assembly, with z_axis (both or neither, perpendicular). Its present axes when absent.")]
    public Vector3? XAxis { get; set; }

    [Description("The component's z axis in the assembly, with x_axis.")]
    public Vector3? ZAxis { get; set; }
}

[Description("The component placed.")]
public sealed class ComponentPlaceResult : IAssemblyChangeResult
{
    [Required]
    [Description("The component, where it is now.")]
    public ComponentInfo Component { get; set; } = new ComponentInfo();

    [Description("True when its mates were solved and moved it from where it was put.")]
    public bool Solved { get; set; }

    [Required]
    [Description("What changed in the assembly.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("component.set", typeof(ComponentSetResult))]
[Description("Changes the state of components in the active configuration: fixed or floating, suppressed or resolved, the configuration of its file each uses, how a subassembly solves (rigid or flexible), hidden or shown. Give at least one property; each component gets every property given. Checked: every property reads back as asked; otherwise every edit is put back.")]
public sealed class ComponentSetRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The components: each a reference (r1.component.…) or a name (bracket-1; sub-1/pin-1 inside a subassembly).")]
    public List<string> Components { get; set; } = new List<string>();

    [Description("Fix in place (true) or float (false).")]
    public bool? Fixed { get; set; }

    [Description("Suppress, or resolve (load in full, as references to its geometry need).")]
    public Suppression? Suppression { get; set; }

    [Description("The configuration of the component's file to use.")]
    public string? Configuration { get; set; }

    [Description("For subassemblies only: solve rigid (as one body) or flexible (its own mates move it).")]
    public SolveAs? SolveAs { get; set; }

    [Description("Hide (true) or show (false).")]
    public bool? Hidden { get; set; }
}

[Description("The components changed.")]
public sealed class ComponentSetResult : IAssemblyChangeResult
{
    [Required]
    [Description("Each component as it is now, in the order asked.")]
    public List<ComponentInfo> Components { get; set; } = new List<ComponentInfo>();

    [Required]
    [Description("What changed in the assembly; changed lists each property that was different before.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("component.delete", typeof(ComponentDeleteResult))]
[Description("Deletes top-level components from an assembly, and with them the mates that use them (listed in mates_deleted). An entry may instead name a component pattern, by its name (LocalLPattern1) or reference (r1.feature.…): the pattern is deleted with every instance it made, and its seeds stay. A pattern's seed is refused unless the pattern is deleted in the same request, and a single pattern instance is refused (delete its pattern). A component inside a subassembly is deleted in that subassembly's own document. Checked: the components, patterns, instances and their mates are gone. A deletion is not rolled back: what SOLIDWORKS deleted stays deleted, and a failed check says what is left.")]
public sealed class ComponentDeleteRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The components or component patterns: each a reference (r1.component.…, r1.feature.…) or a name (bracket-1, LocalLPattern1).")]
    public List<string> Components { get; set; } = new List<string>();
}

[Description("The components and patterns deleted.")]
public sealed class ComponentDeleteResult : IAssemblyChangeResult
{
    [Required]
    [Description("The names of the components deleted, pattern instances not included.")]
    public List<string> Deleted { get; set; } = new List<string>();

    [Required]
    [Description("The names of the mates deleted with them.")]
    public List<string> MatesDeleted { get; set; } = new List<string>();

    [Required]
    [Description("The names of the component patterns deleted.")]
    public List<string> PatternsDeleted { get; set; } = new List<string>();

    [Required]
    [Description("The names of the instances deleted with those patterns.")]
    public List<string> InstancesDeleted { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the assembly.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Description("The component inserted.")]
public sealed class AssemblyInsertResult : IAssemblyChangeResult
{
    [Required]
    [Description("The new component.")]
    public ComponentInfo Component { get; set; } = new ComponentInfo();

    [Required]
    [Description("What changed in the assembly.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}
