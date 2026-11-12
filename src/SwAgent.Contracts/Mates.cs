using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A standard mate's type.")]
public enum MateType
{
    [Description("Two planes, planar faces, straight edges, axes, vertices or points made to touch: in one plane, on one line, a point on a plane.")]
    Coincident,

    [Description("Two planes, planar faces, straight edges, axes or cylinders made parallel.")]
    Parallel,

    [Description("Two planes, planar faces, straight edges, axes or cylinders made perpendicular.")]
    Perpendicular,

    [Description("A cylinder, cone or sphere made to touch a plane, planar face, cylinder, cone or sphere.")]
    Tangent,

    [Description("Two cylinders, cones, circular edges or an axis and one of them made to share an axis.")]
    Concentric,

    [Description("Two components held where they are relative to each other.")]
    Lock,

    [Description("Two planes, planar faces, vertices or points held distance_mm apart.")]
    Distance,

    [Description("Two planes, planar faces, straight edges or axes held angle_deg apart.")]
    Angle,
}

[Description("Which way a mate's two sides face.")]
public enum MateAlignment
{
    [Description("Their normals or axes point the same way.")]
    Aligned,

    [Description("Their normals or axes point opposite ways.")]
    AntiAligned,

    [Description("Whichever is nearer to where the components are now.")]
    Closest,
}

[Description("One side of a mate.")]
public sealed class MateEntity
{
    [Required]
    [Description("A face, edge, vertex, plane, axis or point: a reference issued in the assembly (model.find), name@component for a component's own plane, axis or point (front@bracket-1, Axis1@sub-1/pin-1), or front, top, right or origin for the assembly's own. For lock, a component: its reference or name. With component, a reference issued in that component's own part, or a name there.")]
    public string Entity { get; set; } = "";

    [Description("Only when entity is a reference issued in a component's own part (or a name there): that component, by reference or name (bracket-1, sub-1/pin-1).")]
    public string? Component { get; set; }
}

[Description("A condition a mate imposes, measured between its two sides after the assembly was solved.")]
public sealed class MateMeasurement
{
    [Required]
    [Description("What was measured: distance_mm (between the two; between parallel planes, their offset), angle_deg (between their directions), axis_offset_mm (between two axes), relative_offset_mm and relative_turn_deg (how far a locked component moved and turned relative to the other).")]
    public string Condition { get; set; } = "";

    [Description("What the mate requires.")]
    public double Expected { get; set; }

    [Description("What was measured.")]
    public double Measured { get; set; }

    [Description("How far from expected still holds: 0.001 mm, or 0.01 degrees.")]
    public double Tolerance { get; set; }
}

[Description("A mate of an assembly.")]
public sealed class MateInfo
{
    [Required]
    [Description("Its name as SOLIDWORKS shows it, for example Coincident1.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference (r1.mate.…). References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("Its type, for the standard mates; absent for an advanced or mechanical mate (solid_works_type says which).")]
    public MateType? Type { get; set; }

    [Required]
    [Description("SOLIDWORKS' own name for its type (swMateType_e), for example swMateCOINCIDENT.")]
    public string SolidWorksType { get; set; } = "";

    [Required]
    [Description("The two things it joins, each in the assembly's coordinates and naming its component.")]
    public List<EntityInfo> Entities { get; set; } = new List<EntityInfo>();

    [Description("Which way its sides face, as SOLIDWORKS holds it.")]
    public MateAlignment? Alignment { get; set; }

    [Description("True when its dimension is flipped to the other side.")]
    public bool Flipped { get; set; }

    [Description("A distance mate's distance, mm.")]
    public double? DistanceMm { get; set; }

    [Description("An angle mate's angle, degrees.")]
    public double? AngleDeg { get; set; }

    [Description("True when it is suppressed in the active configuration.")]
    public bool Suppressed { get; set; }

    [Description("SOLIDWORKS' error for it (swFeatureError_e), absent when it has none.")]
    public string? Error { get; set; }
}

[Operation("mate.add", typeof(MateAddResult))]
[Description("Adds a standard mate between two entities of an assembly's components, or two components for lock. Lengths in mm, angles in degrees, everything in the assembly's coordinates. An entity is a reference issued in the assembly (model.find), name@component for a component's planes, axes and points (front@bracket-1, Axis1@sub-1/pin-1), the assembly's own front, top, right or origin, or a reference issued in a component's own part with its component. Checked by measuring after the solve, not by SOLIDWORKS' word: the mate exists without an error, nothing is newly over defined, and its condition holds within 0.001 mm and 0.01 degrees (coincident and tangent touch, concentric axes collinear, parallel at 0, perpendicular at 90, distance and angle at their values, lock leaves the two where they were). Otherwise the mate is deleted and every component the solve moved is put back where it was. measured gives each condition; change.moved lists the components the solve moved. Busy with user_editing while the user is editing a sketch, has a command open or edits a component in context. Place components near where they belong first (component.place): closest alignment depends on where they are. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class MateAddRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("coincident, parallel, perpendicular, tangent, concentric, lock, distance or angle.")]
    public MateType Type { get; set; }

    [Required]
    [Description("The two sides, each from a different component (or one of them the assembly's own plane, axis or point).")]
    public List<MateEntity> Entities { get; set; } = new List<MateEntity>();

    [Description("aligned, anti_aligned or closest. Default closest. Not for lock or perpendicular.")]
    public MateAlignment? Alignment { get; set; }

    [Description("For distance only, required: mm, 0 or more.")]
    public double? DistanceMm { get; set; }

    [Description("For angle only, required: degrees, 0 to 180.")]
    public double? AngleDeg { get; set; }

    [Description("For distance and angle only: put the value on the other side. Default false.")]
    public bool? Flip { get; set; }

    [Description("A name for the mate; SOLIDWORKS names it (Coincident1) when absent.")]
    public string? Name { get; set; }
}

[Description("The mate added.")]
public sealed class MateAddResult : IAssemblyChangeResult
{
    [Required]
    [Description("The new mate, with the alignment SOLIDWORKS chose.")]
    public MateInfo Mate { get; set; } = new MateInfo();

    [Required]
    [Description("Each condition the mate imposes, as measured after the solve.")]
    public List<MateMeasurement> Measured { get; set; } = new List<MateMeasurement>();

    [Required]
    [Description("What changed in the assembly; moved lists the components the solve moved.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("mate.edit", typeof(MateEditResult))]
[Description("Changes a standard mate in place, in the active configuration: a distance mate's distance_mm, an angle mate's angle_deg, the alignment, the flip, or whether it is suppressed. Give at least one. Checked by measuring as mate.add is (a suppressed mate imposes nothing, so only its state is checked); otherwise the old values are put back, and so is every component the solve moved. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class MateEditRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The mate: its reference (r1.mate.…) or its name (Distance1).")]
    public string Mate { get; set; } = "";

    [Description("A distance mate's new distance, mm, 0 or more.")]
    public double? DistanceMm { get; set; }

    [Description("An angle mate's new angle, degrees, 0 to 180.")]
    public double? AngleDeg { get; set; }

    [Description("aligned, anti_aligned or closest. Not for lock or perpendicular.")]
    public MateAlignment? Alignment { get; set; }

    [Description("For distance and angle mates: the value on the other side (true) or not (false).")]
    public bool? Flip { get; set; }

    [Description("Suppress (true) or unsuppress (false) the mate in the active configuration.")]
    public bool? Suppressed { get; set; }
}

[Description("The mate changed.")]
public sealed class MateEditResult : IAssemblyChangeResult
{
    [Required]
    [Description("The mate as it is now.")]
    public MateInfo Mate { get; set; } = new MateInfo();

    [Required]
    [Description("Each condition it imposes, as measured after the solve; empty while it is suppressed, and for lock.")]
    public List<MateMeasurement> Measured { get; set; } = new List<MateMeasurement>();

    [Required]
    [Description("What changed in the assembly; changed lists each property with its value before and after.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("mate.delete", typeof(MateDeleteResult))]
[Description("Deletes mates. The components they joined stay where the mates left them (listed in stayed) and can then move as their other mates allow. Checked: the mates are gone. A deletion is not rolled back: what SOLIDWORKS deleted stays deleted, and a failed check says what is left.")]
public sealed class MateDeleteRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The mates: each a reference (r1.mate.…) or a name (Coincident1).")]
    public List<string> Mates { get; set; } = new List<string>();
}

[Description("The mates deleted.")]
public sealed class MateDeleteResult : IAssemblyChangeResult
{
    [Required]
    [Description("The names of the mates deleted.")]
    public List<string> Deleted { get; set; } = new List<string>();

    [Required]
    [Description("The components they joined, which stay where they are.")]
    public List<string> Stayed { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the assembly.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("mate.list", typeof(MateListResult), ReadOnly = true)]
[Description("Lists an assembly's mates in tree order, all of them or those of one component: name, reference, type (and SOLIDWORKS' own type name for advanced and mechanical mates), the two things each joins (each naming its component, in the assembly's coordinates), alignment, distance or angle, whether it is suppressed, and its error. The assembly's own mates only; a subassembly's are in its own document. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class MateListRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Only the mates of this component: its reference or name (bracket-1).")]
    public string? Component { get; set; }
}

[Description("An assembly's mates.")]
public sealed class MateListResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("Its mates in tree order.")]
    public List<MateInfo> Mates { get; set; } = new List<MateInfo>();
}

[Operation("assembly.dof", typeof(AssemblyDofResult), ReadOnly = true)]
[Description("How far each component's position is defined (fixed, fully_defined, under_defined, over_defined) and, when under defined, how it can still move: free translations and rotations as counts, with their directions (and each rotation's axis point) in the assembly's coordinates, mm. An unmated floating component is free in all six. Free motion is read from SOLIDWORKS (IComponent2.GetRemainingDOFs), never probed by moving anything; when SOLIDWORKS cannot describe it the counts are absent and note says why. SOLIDWORKS 2026 SP4 does not describe it for a component with mates (it answers unavailable), so there only the constrained state is given: counts come for fixed, fully defined and unmated components. The top-level components, or the ones named.")]
public sealed class AssemblyDofRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Only these components: each a reference or a name (bracket-1, sub-1/pin-1). Every top-level component when absent.")]
    public List<string>? Components { get; set; }
}

[Description("Each component's constrained state and free motion.")]
public sealed class AssemblyDofResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("Each component, in the order asked (tree order by default).")]
    public List<ComponentFreedom> Components { get; set; } = new List<ComponentFreedom>();
}

[Description("How a component can still move.")]
public sealed class ComponentFreedom
{
    [Required]
    [Description("The component's name.")]
    public string Name { get; set; } = "";

    [Description("Its reference (r1.component.…).")]
    public string? Reference { get; set; }

    [Description("Fixed, fully defined, under defined, over defined or unknown.")]
    public Constraint Constraint { get; set; }

    [Description("How many ways it can slide; absent when SOLIDWORKS could not say.")]
    public int? FreeTranslations { get; set; }

    [Description("How many ways it can turn; absent when SOLIDWORKS could not say.")]
    public int? FreeRotations { get; set; }

    [Required]
    [Description("The directions it can slide along, unit vectors in the assembly.")]
    public List<Vector3> Translations { get; set; } = new List<Vector3>();

    [Required]
    [Description("The axes it can turn about.")]
    public List<FreeRotation> Rotations { get; set; } = new List<FreeRotation>();

    [Description("Why the counts are absent, or what they mean when they are not SOLIDWORKS' own.")]
    public string? Note { get; set; }
}

[Description("An axis a component can turn about.")]
public sealed class FreeRotation
{
    [Required]
    [Description("Its direction, a unit vector in the assembly.")]
    public Vector3 Axis { get; set; } = new Vector3();

    [Required]
    [Description("A point on it, mm in the assembly.")]
    public Point3 Through { get; set; } = new Point3();
}
