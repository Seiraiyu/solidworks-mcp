using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>An extrude as ISwDocument.Extrude makes it: millimetres and degrees, references already resolved to SOLIDWORKS' objects.</summary>
public sealed class ExtrudeSpec
{
    public bool Cut { get; set; }
    public ExtrudeEnd End { get; set; }

    /// <summary>For blind and mid_plane; 0 otherwise.</summary>
    public double DepthMm { get; set; }

    /// <summary>The face, plane, vertex or point an up_to end names; null otherwise.</summary>
    public object? UpTo { get; set; }

    public bool Reverse { get; set; }

    /// <summary>0 for no draft.</summary>
    public double DraftDeg { get; set; }

    public bool DraftOutward { get; set; }
    public bool Merge { get; set; } = true;

    /// <summary>The second direction, or null for one direction.</summary>
    public ExtrudeSecond? Direction2 { get; set; }

    /// <summary>The thin wall, or null for a solid extrude.</summary>
    public ThinSpec? Thin { get; set; }
}

/// <summary>An extrude's second direction: its end, depth (blind), up-to entity and draft.</summary>
public sealed class ExtrudeSecond
{
    public ExtrudeEnd End { get; set; }
    public double DepthMm { get; set; }
    public object? UpTo { get; set; }
    public double DraftDeg { get; set; }
}

/// <summary>A thin wall: its side and thickness (and the other side's, for two_sides).</summary>
public sealed class ThinSpec
{
    public ThinSide Side { get; set; }
    public double ThicknessMm { get; set; }
    public double Thickness2Mm { get; set; }
}

/// <summary>A revolve as ISwDocument.Revolve makes it: degrees, the axis resolved to SOLIDWORKS' object.</summary>
public sealed class RevolveSpec
{
    public bool Cut { get; set; }

    /// <summary>A line or centerline in the sketch, a reference axis or a straight edge.</summary>
    public object Axis { get; set; } = new object();

    public double AngleDeg { get; set; } = 360;
    public bool Reverse { get; set; }
    public bool Merge { get; set; } = true;
}

/// <summary>A chamfer as ISwDocument.Chamfer makes it: distance and angle, or two distances (Distance2Mm set).</summary>
public sealed class ChamferSpec
{
    public double DistanceMm { get; set; }
    public double AngleDeg { get; set; } = 45;

    /// <summary>For a distance-distance chamfer; null for distance and angle.</summary>
    public double? Distance2Mm { get; set; }
}

/// <summary>A reference plane as ISwDocument.Plane makes it: its references in SOLIDWORKS' order (first, second, third), mm and degrees.</summary>
public sealed class PlaneSpec
{
    public PlaneMode Mode { get; set; }
    public System.Collections.Generic.List<object> References { get; } = new System.Collections.Generic.List<object>();
    public double DistanceMm { get; set; }
    public double AngleDeg { get; set; }
    public bool Flip { get; set; }
}

/// <summary>A reference axis or point as ISwDocument.Axis and ISwDocument.Point make them: the entities resolved, in order.</summary>
public sealed class RefSpec
{
    public System.Collections.Generic.List<object> References { get; } = new System.Collections.Generic.List<object>();

    /// <summary>The axis's mode, for an axis.</summary>
    public AxisMode AxisMode { get; set; }

    /// <summary>The point's mode, for a point.</summary>
    public PointMode PointMode { get; set; }

    /// <summary>For a point along a curve, mm.</summary>
    public double DistanceMm { get; set; }
}

/// <summary>A sweep as ISwDocument.Sweep makes it: the path and guides resolved to SOLIDWORKS' objects.</summary>
public sealed class SweepSpec
{
    public bool Cut { get; set; }

    /// <summary>A sketch, or a model edge.</summary>
    public object Path { get; set; } = new object();

    /// <summary>Guide curve sketches, in order.</summary>
    public System.Collections.Generic.List<object> Guides { get; } = new System.Collections.Generic.List<object>();

    public bool Merge { get; set; } = true;
}

/// <summary>A loft as ISwDocument.Loft makes it: the profiles and guides resolved, in order.</summary>
public sealed class LoftSpec
{
    public bool Cut { get; set; }
    public System.Collections.Generic.List<object> Profiles { get; } = new System.Collections.Generic.List<object>();
    public System.Collections.Generic.List<object> Guides { get; } = new System.Collections.Generic.List<object>();
    public bool Merge { get; set; } = true;
}
