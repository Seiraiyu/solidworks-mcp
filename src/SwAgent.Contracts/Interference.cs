using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("assembly.interference", typeof(AssemblyInterferenceResult), ReadOnly = true)]
[Description("Finds where components of an assembly overlap, with SOLIDWORKS' interference detection over the whole assembly (inside subassemblies too): each pair of components that interfere, the volume (mm³) and the box (mm, assembly coordinates) of the overlap. Faces that only touch are not interference unless coincident_is_interference is true; hidden components are left out unless include_hidden is true. With components, only the interferences that involve one of them (or a component inside one of them) are reported. Changes nothing in the model, and the interference session is always closed, also when SOLIDWORKS fails or the call runs past its deadline. On SOLIDWORKS 2026 SP4 the interference session can mark a saved assembly as having unsaved changes, and the API cannot clear that: then marked_dirty is true, and the assembly is not saved behind you (save it with sw_document_save to clear it, or close it with discard).")]
public sealed class AssemblyInterferenceRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Report only the interferences that involve these components: each a reference (r1.component.…) or a name (bracket-1; sub-1/pin-1 inside a subassembly). Every interference when absent.")]
    public List<string>? Components { get; set; }

    [Description("Count faces that touch as interference (reported with touching true and volume 0). Default false.")]
    public bool? CoincidentIsInterference { get; set; }

    [Description("Include hidden components. Default false.")]
    public bool? IncludeHidden { get; set; }
}

[Description("Two components that overlap.")]
public sealed class InterferenceInfo
{
    [Required]
    [Description("The two components' names (sub-1/pin-1 inside a subassembly).")]
    public List<string> Components { get; set; } = new List<string>();

    [Description("The volume of the overlap, mm³; 0 for faces that only touch.")]
    public double VolumeMm3 { get; set; }

    [Description("The box around the overlap, mm in the assembly; absent when SOLIDWORKS gives none.")]
    public Box3? Box { get; set; }

    [Description("True when the components only touch (coincident faces, no volume).")]
    public bool Touching { get; set; }
}

[Description("The interferences found.")]
public sealed class AssemblyInterferenceResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("Each interference reported, largest volume first.")]
    public List<InterferenceInfo> Interferences { get; set; } = new List<InterferenceInfo>();

    [Description("The sum of their volumes, mm³.")]
    public double TotalVolumeMm3 { get; set; }

    [Description("How many components the assembly has, nested ones included: what SOLIDWORKS checked.")]
    public int Components { get; set; }

    [Description("True when SOLIDWORKS' interference session marked an assembly that had no unsaved changes as having them (seen on SOLIDWORKS 2026 SP4; the API cannot clear the flag). Nothing in the model changed: the components are where they were, and the assembly was not saved to clear the flag.")]
    public bool MarkedDirty { get; set; }
}
