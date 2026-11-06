using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("model.measure", typeof(ModelMeasureResult), ReadOnly = true)]
[Description("Measures between two entities of a part: faces, edges, vertices, planes, axes or points, by reference or name. Gives the shortest distance and the two closest points (mm); the distance between centres where both have one (circles, spheres, vertices, points); and the angle between them where both have a direction (planar faces and planes by their normals, straight edges, axes, cylinders and cones by their axes), 0 to 90 degrees. Read twice: a measurement that changed between the two answers verification_failed. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ModelMeasureRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The first entity: a reference, or a plane's, axis's or point's name (front, top, right and origin included).")]
    public string A { get; set; } = "";

    [Required]
    [Description("The second entity, as a.")]
    public string B { get; set; } = "";
}

[Description("A measurement between two entities.")]
public sealed class ModelMeasureResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The first entity, described.")]
    public EntityInfo A { get; set; } = new EntityInfo();

    [Required]
    [Description("The second entity, described.")]
    public EntityInfo B { get; set; } = new EntityInfo();

    [Description("The shortest distance between them, mm; 0 where they touch.")]
    public double DistanceMm { get; set; }

    [Description("The point on a closest to b, mm.")]
    public Point3? PointA { get; set; }

    [Description("The point on b closest to a, mm.")]
    public Point3? PointB { get; set; }

    [Description("The distance between their centres, mm, where both have one.")]
    public double? CenterDistanceMm { get; set; }

    [Description("The angle between their directions, 0 to 90 degrees, where both have one.")]
    public double? AngleDeg { get; set; }
}
