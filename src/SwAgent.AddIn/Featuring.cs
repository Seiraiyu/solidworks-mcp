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
