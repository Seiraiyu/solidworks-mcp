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
