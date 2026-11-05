using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("model.tree", typeof(ModelTreeResult), ReadOnly = true)]
[Description("Lists a part's features in tree order, sketches after the feature that uses them, each with its reference, whether it is suppressed and its rebuild error or warning. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ModelTreeRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part's feature tree.")]
public sealed class ModelTreeResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("Every feature in tree order.")]
    public List<FeatureNode> Features { get; set; } = new List<FeatureNode>();
}

[Description("A feature in the tree.")]
public sealed class FeatureNode
{
    [Required]
    [Description("Its name, for example Boss-Extrude1.")]
    public string Name { get; set; } = "";

    [Required]
    [Description("SOLIDWORKS' type name for it, for example Extrusion, Cut, Fillet, RefPlane, ProfileFeature (a sketch).")]
    public string Type { get; set; } = "";

    [Description("feature, sketch, plane, axis or point.")]
    public ReferenceKind Kind { get; set; }

    [Description("Its persistent reference; absent when SOLIDWORKS gives none. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("The feature it sits under (a sketch under the extrude that uses it); absent at the top level.")]
    public string? Parent { get; set; }

    [Description("True when it is suppressed in the active configuration.")]
    public bool Suppressed { get; set; }

    [Description("Its rebuild error or warning code (swFeatureError_e name); absent when it rebuilt cleanly.")]
    public string? Error { get; set; }

    [Description("True when Error is only a warning.")]
    public bool Warning { get; set; }
}

[Operation("model.mass", typeof(ModelMassResult), ReadOnly = true)]
[Description("Mass properties of a part's solid bodies, in mm, g and the part's own material: volume, surface area, mass, density, centre of mass and principal moments of inertia.")]
public sealed class ModelMassRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part's mass properties. All zero, with no centre of mass, when it has no solid body.")]
public sealed class ModelMassResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("How many solid bodies it has.")]
    public int Bodies { get; set; }

    [Description("Volume, mm³.")]
    public double VolumeMm3 { get; set; }

    [Description("Surface area, mm².")]
    public double AreaMm2 { get; set; }

    [Description("Mass, g.")]
    public double MassG { get; set; }

    [Description("Density used, kg/m³ (SOLIDWORKS' default when no material is set).")]
    public double DensityKgM3 { get; set; }

    [Description("The material's name; absent when none is set.")]
    public string? Material { get; set; }

    [Description("Centre of mass, mm.")]
    public Point3? CenterOfMass { get; set; }

    [Required]
    [Description("Principal moments of inertia at the centre of mass, g·mm², smallest first.")]
    public List<double> PrincipalMomentsGMm2 { get; set; } = new List<double>();
}

[Operation("model.bounding_box", typeof(ModelBoundingBoxResult), ReadOnly = true)]
[Description("The axis-aligned box around a part's solid bodies, in mm. SOLIDWORKS' quick box by default (it may be a little larger than the bodies); precise for the exact one.")]
public sealed class ModelBoundingBoxRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Compute the exact box from the bodies' extreme points. Default false.")]
    public bool? Precise { get; set; }
}

[Description("A part's bounding box. No box when it has no solid body.")]
public sealed class ModelBoundingBoxResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("How many solid bodies it has.")]
    public int Bodies { get; set; }

    [Description("The box, mm.")]
    public Box3? Box { get; set; }

    [Description("Its size along X, mm.")]
    public double SizeX { get; set; }

    [Description("Its size along Y, mm.")]
    public double SizeY { get; set; }

    [Description("Its size along Z, mm.")]
    public double SizeZ { get; set; }

    [Description("True when the box is exact.")]
    public bool Precise { get; set; }
}

[Operation("model.find", typeof(ModelFindResult), ReadOnly = true)]
[Description("Finds a part's faces, edges or vertices by what they are, each with its persistent reference and shape: by the feature that made them, their geometry, a direction (a planar face's outward normal, or an edge's or cylinder's axis either way), or a point they contain or are near. Use it to get references to pass to other operations, such as the top face of Boss-Extrude1 or the circular edges of a hole. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ModelFindRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("What to find: face, edge or vertex.")]
    public TopologyKind Kind { get; set; }

    [Description("Only what this feature made (for an edge or vertex, any feature of a face it lies on): its name, or a reference exactly as a result gave it.")]
    public string? Feature { get; set; }

    [Description("Only this shape, for example plane or cylinder for faces, line or circle for edges.")]
    public GeometryType? Geometry { get; set; }

    [Description("Only faces whose outward normal points this way, or edges, cylinders and cones whose axis is parallel to it.")]
    public Vector3? Direction { get; set; }

    [Description("How far from direction still counts, degrees, 0 to 90. Default 1.")]
    public double? AngleToleranceDeg { get; set; }

    [Description("Only what is within max_distance_mm of this point (mm), nearest first.")]
    public Point3? Near { get; set; }

    [Description("How far from near still counts, mm. Default 1. Needs near.")]
    public double? MaxDistanceMm { get; set; }

    [Description("At most this many, 1 to 1000. Default 50.")]
    public int? Limit { get; set; }
}

[Description("What model.find found.")]
public sealed class ModelFindResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("What matched, nearest first when near was given, else in SOLIDWORKS' order; at most limit.")]
    public List<EntityInfo> Entities { get; set; } = new List<EntityInfo>();

    [Description("How many matched in all.")]
    public int Total { get; set; }

    [Description("True when more matched than were returned; narrow the search or raise limit.")]
    public bool Truncated { get; set; }
}
