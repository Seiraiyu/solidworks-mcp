using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

/// <summary>The topology model.find searches.</summary>
public enum TopologyKind
{
    Face,
    Edge,
    Vertex,
}

/// <summary>The shape of a face, edge, plane or point.</summary>
public enum GeometryType
{
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
    Line,
    Circle,
    Ellipse,
    Spline,
    Point,
    Other,
}

[Description("A face, edge, vertex, feature, plane, axis or point, with its reference and a short geometric description. Lengths in mm, areas in mm².")]
public sealed class EntityInfo
{
    [Description("What it is.")]
    public ReferenceKind Kind { get; set; }

    [Description("Its persistent reference (r1.<kind>.…), to pass to other operations; absent when SOLIDWORKS cannot give one. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Description("Its name, for features, planes, axes and points.")]
    public string? Name { get; set; }

    [Description("The feature that made it, for faces, edges and vertices. An edge or vertex where several features meet names the first; see features.")]
    public string? Feature { get; set; }

    [Description("For edges and vertices, every feature that made a face it lies on: the circular edge of a hole cut in a boss belongs to both.")]
    public List<string>? Features { get; set; }

    [Description("Its shape: plane, cylinder, cone, sphere, torus, line, circle, ellipse, spline, point or other.")]
    public GeometryType Geometry { get; set; }

    [Description("Area of a face, mm².")]
    public double? AreaMm2 { get; set; }

    [Description("Length of an edge, mm.")]
    public double? LengthMm { get; set; }

    [Description("Outward normal of a planar face, or the normal of a plane.")]
    public Vector3? Normal { get; set; }

    [Description("Axis of a cylinder, cone, circle or axis; direction of a line.")]
    public Vector3? Axis { get; set; }

    [Description("A point on it: a planar face's or edge's centre, a circle's or cylinder's centre on its axis, a vertex or point itself.")]
    public Point3? Origin { get; set; }

    [Description("Radius of a cylinder, cone base, sphere, torus or circle, mm.")]
    public double? RadiusMm { get; set; }

    [Description("Its axis-aligned bounding box, for faces only.")]
    public Box3? Box { get; set; }

    [Description("Distance from the point model.find was asked to be near, mm.")]
    public double? DistanceMm { get; set; }
}
