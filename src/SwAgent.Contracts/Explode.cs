using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("One step of an exploded view: components moved along a direction, and optionally turned about an axis afterwards.")]
public sealed class ExplodeStep
{
    [Required]
    [Description("The components to move: each a reference (r1.component.…) or a name (pin-1). Top-level, resolved components.")]
    public List<string> Components { get; set; } = new List<string>();

    [Required]
    [Description("Which way to move them: x, y or z (the assembly's own axes, the normals of its right, top and front planes), or a straight edge, an axis, or a plane or planar face (along its normal), in the assembly: a reference issued in the assembly (model.find), name@component (Axis1@plate-1), or with direction_component a reference issued in that component's own part.")]
    public string Direction { get; set; } = "";

    [Description("Only when direction is a reference issued in a component's own part (or a name there): that component, by reference or name.")]
    public string? DirectionComponent { get; set; }

    [Description("How far to move them, mm, more than 0 and at most 10000.")]
    public double? DistanceMm { get; set; }

    [Description("Go the other way along the direction. Default false.")]
    public bool? Reverse { get; set; }

    [Description("An axis to turn them about once moved: an axis, a straight or circular edge, or a cylindrical or conical face, in the forms direction takes. With angle_deg.")]
    public string? RotateAbout { get; set; }

    [Description("Only when rotate_about is a reference issued in a component's own part (or a name there): that component, by reference or name.")]
    public string? RotateAboutComponent { get; set; }

    [Description("How far to turn them about rotate_about, degrees, more than 0 and at most 360. With rotate_about.")]
    public double? AngleDeg { get; set; }
}

[Operation("explode.create", typeof(ExplodeCreateResult))]
[Description("Makes an exploded view of an assembly in one call (SOLIDWORKS' Explode): a new view in the active configuration holding every step given, in order. Lengths in mm, angles in degrees. A step's direction is x, y or z (the assembly's axes), a straight edge, an axis, or a plane or planar face's normal: a reference issued in the assembly, name@component (Axis1@plate-1), or with direction_component a reference issued in that component's own part. Checked step by step: after each step the view is shown exploded and every component read; the step's components must have moved distance_mm along the direction from where the previous steps left them, then turned angle_deg about rotate_about, within 0.001 mm and 0.01 degrees, and no other component may have moved. SOLIDWORKS chooses which way along a direction and about an axis it goes; each step's sense says which, and reverse flips it. Then the assembly is collapsed again (or shows what it showed before) and every component must be back where it was. Otherwise the view is deleted (rolled back). Show it with explode.show, list it with explode.list, delete it with explode.delete. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ExplodeCreateRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("The configuration to make the view in; it must be the active one (configuration.activate changes that). The active configuration when absent.")]
    public string? Configuration { get; set; }

    [Description("The view's name; SOLIDWORKS' own (ExplView1) when absent.")]
    public string? Name { get; set; }

    [Required]
    [Description("The steps, in order: 1 to 20.")]
    public List<ExplodeStep> Steps { get; set; } = new List<ExplodeStep>();
}

[Description("A component an explode step moved, and how far it is from where it should be.")]
public sealed class ExplodedComponent
{
    [Required]
    [Description("The component's name.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("Where its origin was before the step, mm in the assembly.")]
    public Point3 Before { get; set; } = new Point3();

    [Required]
    [Description("Where its origin should be after the step, mm in the assembly.")]
    public Point3 Expected { get; set; } = new Point3();

    [Required]
    [Description("Where its origin is after the step, mm in the assembly.")]
    public Point3 Actual { get; set; } = new Point3();

    [Description("How far it is from where it should be, mm.")]
    public double OffsetMm { get; set; }

    [Description("How far its axes are turned from how they should be, degrees.")]
    public double TurnDeg { get; set; }
}

[Description("How one explode step went: which way SOLIDWORKS took it, and where each of its components landed.")]
public sealed class ExplodeStepOutcome
{
    [Description("Its position in steps, from 0.")]
    public int Index { get; set; }

    [Description("Its name as SOLIDWORKS shows it, for example Explode Step1.")]
    public string? Name { get; set; }

    [Required]
    [Description("Which way it moved: along or against the direction as described (model.find; x, y and z point +X, +Y and +Z); reverse flips it.")]
    public string Sense { get; set; } = "";

    [Description("Which way it turned: along (right-handed about rotate_about as described) or against; absent without a turn.")]
    public string? TurnSense { get; set; }

    [Required]
    [Description("Each component it moved, where it was, where it should be and where it is.")]
    public List<ExplodedComponent> Components { get; set; } = new List<ExplodedComponent>();
}

[Description("A step of an exploded view, as SOLIDWORKS holds it.")]
public sealed class ExplodeStepInfo
{
    [Required]
    [Description("Its name, for example Explode Step1.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("The names of the components it moves.")]
    public List<string> Components { get; set; } = new List<string>();

    [Description("How far it moves them, mm.")]
    public double DistanceMm { get; set; }

    [Description("How far it turns them, degrees; absent when it does not turn them.")]
    public double? AngleDeg { get; set; }

    [Description("True when it goes against the direction it was given.")]
    public bool Reversed { get; set; }
}

[Description("An exploded view of an assembly.")]
public sealed class ExplodedViewInfo
{
    [Required]
    [Description("Its name, for example ExplView1.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("The configuration it belongs to.")]
    public string Configuration { get; set; } = "";

    [Description("True when the assembly shows it exploded now.")]
    public bool Exploded { get; set; }

    [Description("Its steps, in order; absent for a view of a configuration that is not active, whose steps SOLIDWORKS gives only while it is shown.")]
    public List<ExplodeStepInfo>? Steps { get; set; }
}

[Description("The exploded view made.")]
public sealed class ExplodeCreateResult : IAssemblyChangeResult
{
    [Required]
    [Description("The view, as read back after it was made.")]
    public ExplodedViewInfo View { get; set; } = new ExplodedViewInfo();

    [Required]
    [Description("How each step went, in order.")]
    public List<ExplodeStepOutcome> Steps { get; set; } = new List<ExplodeStepOutcome>();

    [Description("How SOLIDWORKS was asked for each step (the marks it took), for the log.")]
    public string? Tried { get; set; }

    [Required]
    [Description("What changed in the assembly: created holds the view. moved is empty, since the assembly is collapsed again.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

[Operation("explode.list", typeof(ExplodeListResult), ReadOnly = true)]
[Description("Lists an assembly's exploded views, by configuration, with their steps (the components each moves, its distance in mm and angle in degrees). SOLIDWORKS gives a view's steps only while it is shown, so each view of the active configuration is shown for a moment and the assembly then shows what it showed before; views of other configurations are listed without steps. Changes nothing.")]
public sealed class ExplodeListRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("The exploded views of an assembly.")]
public sealed class ExplodeListResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The active configuration.")]
    public string Configuration { get; set; } = "";

    [Required]
    [Description("Every view, configuration by configuration.")]
    public List<ExplodedViewInfo> Views { get; set; } = new List<ExplodedViewInfo>();

    [Description("The view shown exploded now; absent when the assembly is collapsed.")]
    public string? Exploded { get; set; }
}

[Operation("explode.show", typeof(ExplodeShowResult))]
[Description("Explodes an assembly to one of its exploded views of the active configuration, or collapses it. Changes what is shown only, not the assembly, so nothing is rolled back. Waits, like a change, while the user edits a sketch, has a command open or edits a component in context. Checked: the assembly shows the view exploded, or no view, as asked.")]
public sealed class ExplodeShowRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view's name, for example ExplView1 (explode.list lists them).")]
    public string View { get; set; } = "";

    [Description("true to explode, false to collapse. Default true.")]
    public bool? Exploded { get; set; }
}

[Description("What the assembly shows now.")]
public sealed class ExplodeShowResult
{
    [Required]
    [Description("The assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The view.")]
    public string View { get; set; } = "";

    [Description("True when the assembly shows it exploded now.")]
    public bool Exploded { get; set; }

    [Description("The view shown exploded before the call; absent when the assembly was collapsed.")]
    public string? Before { get; set; }
}

[Operation("explode.delete", typeof(ExplodeDeleteResult))]
[Description("Deletes an exploded view of the active configuration and its steps; a view shown exploded is collapsed first. Components stay where they are collapsed. Checked: the view is gone. A deletion is not rolled back: a failed check says what is left.")]
public sealed class ExplodeDeleteRequest
{
    [Description("The assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view's name, for example ExplView1 (explode.list lists them).")]
    public string View { get; set; } = "";
}

[Description("The exploded view deleted.")]
public sealed class ExplodeDeleteResult : IAssemblyChangeResult
{
    [Required]
    [Description("The view's name.")]
    public string Deleted { get; set; } = "";

    [Required]
    [Description("The configuration it belonged to.")]
    public string Configuration { get; set; } = "";

    [Description("How many steps it had.")]
    public int Steps { get; set; }

    [Required]
    [Description("What changed in the assembly.")]
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}
