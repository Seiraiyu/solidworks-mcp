using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("Which kind of component pattern.")]
public enum ComponentPatternKind
{
    [Description("Rows along a direction, and columns along a second one.")]
    Linear,

    [Description("Around an axis.")]
    Circular,

    [Description("Driven by a part's pattern or Hole Wizard feature: one instance on each of its holes.")]
    Feature,
}

[Description("A linear component pattern's second direction: its own direction, spacing and count.")]
public sealed class ComponentPatternDirection
{
    [Required]
    [Description("A straight edge, an axis, or a plane or planar face (along its normal), in the assembly: a reference issued in the assembly (model.find), name@component for a component's own plane or axis (Axis1@plate-1), or front, top or right for the assembly's own. With direction_component, a reference issued in that component's own part, or a name there.")]
    public string Direction { get; set; } = "";

    [Description("Only when direction is a reference issued in a component's own part (or a name there): that component, by reference or name.")]
    public string? DirectionComponent { get; set; }

    [Description("The distance between instances, mm, more than 0 and at most 10000.")]
    public double? SpacingMm { get; set; }

    [Description("How many instances in this direction, the seed's row included: 2 to 500.")]
    public int? Count { get; set; }

    [Description("Go the other way along the direction. Default false.")]
    public bool? Reverse { get; set; }
}

[Operation("component.linear_pattern", typeof(ComponentPatternResult))]
[Description("Repeats components in rows along a direction, and optionally in columns along a second one (a component pattern; SOLIDWORKS' LocalLPattern). Lengths in mm. The direction is a straight edge, an axis, or a plane or planar face (along its normal): a reference issued in the assembly, name@component (Axis1@plate-1), or with direction_component a reference issued in that component's own part. The seeds must be top-level, resolved components that are not themselves pattern instances. Checked: the pattern has the instances asked for, and each one sits at its seed's position moved by spacing × i along the direction (and spacing × j along the second), axes unchanged, within 0.001 mm and 0.01 degrees; SOLIDWORKS chooses which way along an edge to go, and sense says which (reverse flips it). Otherwise the pattern is deleted and every moved component put back (rolled back). Delete it later with component.delete and its name or reference. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ComponentLinearPatternRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The components to repeat (the seeds): each a reference (r1.component.…) or a name (pin-1).")]
    public List<string> Components { get; set; } = new List<string>();

    [Required]
    [Description("The first direction: a straight edge, an axis, or a plane or planar face (along its normal). See the operation's description for the forms it takes.")]
    public string Direction { get; set; } = "";

    [Description("Only when direction is a reference issued in a component's own part (or a name there): that component, by reference or name.")]
    public string? DirectionComponent { get; set; }

    [Description("The distance between instances, mm, more than 0 and at most 10000.")]
    public double? SpacingMm { get; set; }

    [Description("How many instances along the direction, the seed included: 2 to 500.")]
    public int? Count { get; set; }

    [Description("Go the other way along the direction. Default false.")]
    public bool? Reverse { get; set; }

    [Description("A second direction, making a grid.")]
    public ComponentPatternDirection? Direction2 { get; set; }
}

[Operation("component.circular_pattern", typeof(ComponentPatternResult))]
[Description("Repeats components around an axis (a component pattern; SOLIDWORKS' LocalCirPattern). Angles in degrees. The axis is an axis, a straight or circular edge, or a cylindrical or conical face (its axis): a reference issued in the assembly, name@component (Axis1@plate-1), or with axis_component a reference issued in that component's own part. With equal_spacing (the default) the instances share angle_deg (360 unless given): 360 is split into count steps, any smaller angle into count - 1; without it, angle_deg is the angle from one instance to the next. The seeds must be top-level, resolved components that are not themselves pattern instances. Checked: the pattern has the instances asked for, and each one is its seed turned about the axis by the step × i, origin and axes, within 0.001 mm and 0.01 degrees; sense says which way it turned (reverse flips it). Otherwise the pattern is deleted and every moved component put back (rolled back). Delete it later with component.delete. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ComponentCircularPatternRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The components to repeat (the seeds): each a reference (r1.component.…) or a name (pin-1).")]
    public List<string> Components { get; set; } = new List<string>();

    [Required]
    [Description("The axis to turn about. See the operation's description for the forms it takes.")]
    public string Axis { get; set; } = "";

    [Description("Only when axis is a reference issued in a component's own part (or a name there): that component, by reference or name.")]
    public string? AxisComponent { get; set; }

    [Description("How many instances, the seed included: 2 to 500.")]
    public int? Count { get; set; }

    [Description("With equal_spacing, the angle the instances share (default 360); without it, the angle between neighbours. Degrees, more than 0 and at most 360.")]
    public double? AngleDeg { get; set; }

    [Description("Share angle_deg equally among the instances. Default true.")]
    public bool? EqualSpacing { get; set; }

    [Description("Turn the other way. Default false.")]
    public bool? Reverse { get; set; }
}

[Operation("component.feature_pattern", typeof(ComponentPatternResult))]
[Description("Puts one instance of the components on every hole of a part's pattern or Hole Wizard feature (a pattern driven component pattern), so one fastener becomes one in every hole. driving_feature names the feature in the assembly: name@component (CirPattern1@plate-1, CSK for M5 Flat Head Machine Screw1@plate-1) or a reference issued in the assembly. Each seed must already sit on one of the feature's holes (place or mate it there first); the feature must make at least two holes (cylindrical faces), since they are what the check measures. Checked: every other hole gets exactly one instance of each seed, at the seed's distance from its hole's axis and the seed's height along it, within 0.001 mm. Otherwise the pattern is deleted and every moved component put back (rolled back). Delete it later with component.delete. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ComponentFeaturePatternRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The components to repeat (the seeds), each sitting on one of the driving feature's holes: a reference (r1.component.…) or a name (screw-1).")]
    public List<string> Components { get; set; } = new List<string>();

    [Required]
    [Description("The part's pattern or Hole Wizard feature, in the assembly: name@component (CirPattern1@plate-1) or a reference issued in the assembly.")]
    public string DrivingFeature { get; set; } = "";
}

[Description("A component pattern of an assembly.")]
public sealed class ComponentPatternInfo
{
    [Required]
    [Description("Its name as SOLIDWORKS shows it, for example LocalLPattern1.")]
    public string Name { get; set; } = "";

    [Description("Its persistent reference (r1.feature.…), for component.delete. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("Linear, circular or feature-driven.")]
    public ComponentPatternKind Kind { get; set; }

    [Required]
    [Description("The names of the components it repeats.")]
    public List<string> Seeds { get; set; } = new List<string>();

    [Required]
    [Description("The names of the components it made, the seeds not included.")]
    public List<string> Instances { get; set; } = new List<string>();

    [Description("Its swFeatureError_e name when it has a rebuild error; absent when it rebuilt cleanly.")]
    public string? Error { get; set; }
}

[Description("An instance a pattern made, and how far it is from where it should be.")]
public sealed class PatternInstance
{
    [Required]
    [Description("The instance's component name.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("The seed it repeats.")]
    public string Seed { get; set; } = "";

    [Required]
    [Description("Where its origin is, mm in the assembly.")]
    public Point3 Origin { get; set; } = new Point3();

    [Required]
    [Description("Where its origin should be, mm in the assembly.")]
    public Point3 Expected { get; set; } = new Point3();

    [Description("How far it is from where it should be, mm (for a feature pattern, the larger of its error in distance from the hole's axis and in height along it).")]
    public double OffsetMm { get; set; }

    [Description("How far its axes are turned from where they should be, degrees; absent for a feature pattern, whose instances may turn.")]
    public double? TurnDeg { get; set; }
}

[Description("The component pattern made.")]
public sealed class ComponentPatternResult : IAssemblyChangeResult
{
    [Required]
    [Description("The pattern.")]
    public ComponentPatternInfo Pattern { get; set; } = new ComponentPatternInfo();

    [Required]
    [Description("Each instance with where it landed and where it should be.")]
    public List<PatternInstance> Instances { get; set; } = new List<PatternInstance>();

    [Description("For linear and circular patterns, which way SOLIDWORKS went: along or against the direction (or each direction, along/against) or axis as described in model.find; reverse flips it.")]
    public string? Sense { get; set; }

    [Description("For a feature pattern, how many holes the driving feature makes.")]
    public int? Holes { get; set; }

    [Required]
    [Description("What changed in the assembly: created holds the pattern.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}
