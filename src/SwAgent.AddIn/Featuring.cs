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

/// <summary>A rib as ISwDocument.Rib makes it: mm and degrees.</summary>
public sealed class RibSpec
{
    public double ThicknessMm { get; set; }
    public RibSide Side { get; set; }
    public bool NormalToSketch { get; set; }
    public bool FlipMaterial { get; set; }

    /// <summary>0 for no draft.</summary>
    public double DraftDeg { get; set; }

    public bool DraftOutward { get; set; }
}

/// <summary>A neutral-plane draft as ISwDocument.Draft makes it: degrees, the plane and faces resolved.</summary>
public sealed class DraftSpec
{
    /// <summary>A plane feature or a planar face.</summary>
    public object Neutral { get; set; } = new object();

    public System.Collections.Generic.List<object> Faces { get; } = new System.Collections.Generic.List<object>();
    public double AngleDeg { get; set; }
    public bool Reverse { get; set; }
}

/// <summary>A Hole Wizard hole as ISwDocument.Hole makes it: the face, the sketch points at its positions, mm.</summary>
public sealed class HoleSpec
{
    public object Face { get; set; } = new object();

    /// <summary>The sketch points the operation drew at the positions, in their order.</summary>
    public System.Collections.Generic.List<object> Points { get; } = new System.Collections.Generic.List<object>();

    /// <summary>The positions in model space, mm, in the same order.</summary>
    public System.Collections.Generic.List<Point3> Positions { get; } = new System.Collections.Generic.List<Point3>();

    public HoleType Type { get; set; }
    public HoleStandard Standard { get; set; }
    public string Size { get; set; } = "";
    public HoleEnd End { get; set; }

    /// <summary>For blind; 0 for through_all.</summary>
    public double DepthMm { get; set; }
}

/// <summary>A linear pattern as ISwDocument.LinearPattern makes it: the seeds and directions resolved, mm.</summary>
public sealed class LinearPatternSpec
{
    public System.Collections.Generic.List<object> Seeds { get; } = new System.Collections.Generic.List<object>();

    /// <summary>Faces to repeat instead of seeds (a geometry pattern).</summary>
    public System.Collections.Generic.List<object> Faces { get; } = new System.Collections.Generic.List<object>();

    public object Direction { get; set; } = new object();
    public double SpacingMm { get; set; }
    public int Count { get; set; }
    public bool Reverse { get; set; }

    /// <summary>The second direction, or null for one row.</summary>
    public object? Direction2 { get; set; }

    public double SpacingMm2 { get; set; }

    /// <summary>1 without a second direction.</summary>
    public int Count2 { get; set; } = 1;

    public bool Reverse2 { get; set; }
}

/// <summary>A circular pattern as ISwDocument.CircularPattern makes it: the seeds and axis resolved, degrees.</summary>
public sealed class CircularPatternSpec
{
    public System.Collections.Generic.List<object> Seeds { get; } = new System.Collections.Generic.List<object>();

    /// <summary>Faces to repeat instead of seeds (a geometry pattern).</summary>
    public System.Collections.Generic.List<object> Faces { get; } = new System.Collections.Generic.List<object>();

    public object Axis { get; set; } = new object();
    public int Count { get; set; }

    /// <summary>The total angle with equal spacing; the angle between neighbours without.</summary>
    public double AngleDeg { get; set; } = 360;

    public bool EqualSpacing { get; set; } = true;
    public bool Reverse { get; set; }
}
