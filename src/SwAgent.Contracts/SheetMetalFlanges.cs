using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

/// <summary>
/// Where a flange's bend sits relative to the edge it is made on (SOLIDWORKS' flange position). bend_centerline is not
/// offered: CADapter measured it a silent no-op on the sketched bend, and the flanges have not been proven with it.
/// </summary>
public enum BendPosition
{
    /// <summary>The flange's outside face stays within the edge: the sheet's outline does not grow.</summary>
    MaterialInside,

    /// <summary>The flange's inside face stays on the edge: the flange's thickness is outside it.</summary>
    MaterialOutside,

    /// <summary>The bend starts at the edge: the bend and the flange are outside it.</summary>
    BendOutside,
}

/// <summary>Where an edge flange's length is measured from.</summary>
public enum FlangeLengthFrom
{
    /// <summary>From where the outside faces of the sheet and the flange would meet without the bend.</summary>
    OuterVirtualSharp,

    /// <summary>From where the inside faces of the sheet and the flange would meet without the bend.</summary>
    InnerVirtualSharp,

    /// <summary>From where the bend ends: the flange's straight part only.</summary>
    Tangent,
}

/// <summary>The shape of a hem: the edge folded back on itself.</summary>
public enum HemKind
{
    /// <summary>Folded flat onto the sheet, length_mm long.</summary>
    Closed,

    /// <summary>Folded back with gap_mm between it and the sheet, length_mm long.</summary>
    Open,

    /// <summary>Rolled through angle_deg at radius_mm, closing back towards the sheet like a teardrop.</summary>
    Teardrop,

    /// <summary>Rolled through angle_deg at radius_mm, open.</summary>
    Rolled,
}
