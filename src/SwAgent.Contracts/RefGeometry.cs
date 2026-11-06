using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

/// <summary>How a reference plane is placed.</summary>
public enum PlaneMode
{
    Offset,
    Angle,
    ThreePoints,
    ParallelThroughPoint,
    NormalToCurve,
}

[Operation("refgeom.plane", typeof(FeatureResult))]
[Description("Makes a reference plane: offset (from, distance_mm, flip), angle (from, axis, angle_deg, flip), three_points (points), parallel_through_point (from, through) or normal_to_curve (curve, through). Lengths in mm, angles in degrees. Checked against the request: the plane exists, the part's volume is unchanged, and its normal and position are what was asked (the offset from the plane or face, the angle to it, the points on it, the curve normal to it). Otherwise it is deleted (rolled back). Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class RefgeomPlaneRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("offset, angle, three_points, parallel_through_point or normal_to_curve.")]
    public PlaneMode Mode { get; set; }

    [Description("For offset, angle and parallel_through_point: the plane or planar face it is placed from (front, top, right, a plane's name, or a reference).")]
    public string? From { get; set; }

    [Description("For offset: how far from it, mm, more than 0.")]
    public double? DistanceMm { get; set; }

    [Description("For offset and angle: the other side, or the other way round. Default false.")]
    public bool? Flip { get; set; }

    [Description("For angle: the straight edge or axis, lying in from, that it turns about.")]
    public string? Axis { get; set; }

    [Description("For angle: the angle to from, degrees, more than 0 and less than 180.")]
    public double? AngleDeg { get; set; }

    [Description("For three_points: three vertices or points, not in a line.")]
    public List<string>? Points { get; set; }

    [Description("For parallel_through_point and normal_to_curve: the vertex or point it passes through.")]
    public string? Through { get; set; }

    [Description("For normal_to_curve: the edge it is normal to, at through.")]
    public string? Curve { get; set; }
}
