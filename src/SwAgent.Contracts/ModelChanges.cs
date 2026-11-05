using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A point in a sketch's own plane, in millimetres: x along the sketch's x axis, y along its y axis.")]
public sealed class Point2
{
    public Point2()
    {
    }

    public Point2(double x, double y)
    {
        X = x;
        Y = y;
    }

    [Description("X in the sketch, mm.")]
    public double X { get; set; }

    [Description("Y in the sketch, mm.")]
    public double Y { get; set; }

    public override string ToString() => System.FormattableString.Invariant($"({X:0.###}, {Y:0.###})");
}

/// <summary>A result that carries the evidence block every model-changing operation returns.</summary>
public interface IModelChangeResult
{
    ModelChange Change { get; set; }
}

[Description("The evidence every model-changing operation returns (design 371, Model changes): what it made and edited, and the part measured after it.")]
public sealed class ModelChange
{
    [Required]
    [Description("The part it acted on.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("What it made (features, sketches, sketch entities), in the order it made them.")]
    public List<CreatedItem> Created { get; set; } = new List<CreatedItem>();

    [Required]
    [Description("What it edited, each with its value before and after.")]
    public List<ChangedItem> Changed { get; set; } = new List<ChangedItem>();

    [Required]
    [Description("The part's rebuild errors and warnings after the change, by feature.")]
    public RebuildStatus Rebuild { get; set; } = new RebuildStatus();

    [Description("Solid bodies before the change.")]
    public int BodiesBefore { get; set; }

    [Description("Solid bodies after the change.")]
    public int BodiesAfter { get; set; }

    [Description("Volume of the solid bodies before the change, mm³.")]
    public double VolumeBeforeMm3 { get; set; }

    [Description("Volume of the solid bodies after the change, mm³.")]
    public double VolumeAfterMm3 { get; set; }

    [Description("The box around the solid bodies after the change (SOLIDWORKS' quick box), mm; absent when the part has no solid body.")]
    public Box3? Box { get; set; }
}

[Description("Something a model-changing operation made.")]
public sealed class CreatedItem
{
    [Required]
    [Description("Its name, for example Sketch3 or Line2.")]
    public string Name { get; set; } = "";

    [Description("What it is: feature, sketch, sketch_entity, sketch_point, dimension, plane, axis or point.")]
    public ReferenceKind Kind { get; set; }

    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }
}

[Description("A value a model-changing operation edited.")]
public sealed class ChangedItem
{
    [Required]
    [Description("What was edited, for example D1@Sketch1.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("Which of its properties: value, or suppressed.")]
    public string Property { get; set; } = "";

    [Required]
    [Description("Its value before, with its unit, for example 40 mm.")]
    public string Before { get; set; } = "";

    [Required]
    [Description("Its value after, with its unit.")]
    public string After { get; set; } = "";
}

[Description("A part's rebuild errors and warnings.")]
public sealed class RebuildStatus
{
    [Description("How many features have a rebuild error.")]
    public int Errors { get; set; }

    [Description("How many features have a rebuild warning.")]
    public int Warnings { get; set; }

    [Required]
    [Description("Each feature with an error or warning.")]
    public List<RebuildItem> Items { get; set; } = new List<RebuildItem>();
}
