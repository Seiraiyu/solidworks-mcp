using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Edge flanges, miter flanges and hems (design 454, Flanges and hems), through the calls CADapter measured building on
/// 2026 SP4: InsertSketchForEdgeFlange with a profile drawn into it and the one-edge InsertSheetMetalEdgeFlange; the miter
/// flange's sketch selected and its edge appended; InsertSheetMetalHem2 on the selected edges. Proven by the sheet-metal
/// live suite, not by unit tests, and never trusted on their answer: the operations measure what was made.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    public IReadOnlyList<object> AdjacentFaces(object edge) =>
        ((IEdge)edge).GetTwoAdjacentFaces2() is object[] faces ? faces.Where(f => f != null).ToList() : new List<object>();

    public object? EdgeFlangeSketch(object edge, double angleDeg)
    {
        Doc.ClearSelection2(true);
        try
        {
            return Doc.InsertSketchForEdgeFlange(edge, angleDeg * Rad, false) as IFeature;
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    static int Position(BendPosition position) => position switch
    {
        BendPosition.MaterialOutside => (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialOutside,
        BendPosition.BendOutside => (int)swFlangePositionTypes_e.swFlangePositionTypeBendOutside,
        _ => (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside,
    };

    static int LengthFrom(FlangeLengthFrom from) => from switch
    {
        FlangeLengthFrom.InnerVirtualSharp => (int)swFlangeDimTypes_e.swFlangeDimTypeInnerVirtualSharp,
        FlangeLengthFrom.Tangent => (int)swFlangeDimTypes_e.swFlangeDimTypeBendTangentArc,
        _ => (int)swFlangeDimTypes_e.swFlangeDimTypeOuterVirtualSharp,
    };

    /// <summary>
    /// InsertSheetMetalEdgeFlange (the one-edge version CADapter measured): the default relief, the default radius unless
    /// one is given, no offset, the allowance as a CustomBendAllowance when one is given.
    /// </summary>
    public object? EdgeFlange(object edge, object sketch, EdgeFlangeSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            var fm = Doc.FeatureManager;
            int options = (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief
                | (s.RadiusMm == null ? (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius : 0);
            var cba = s.Allowance == null ? null : Write(fm.CreateCustomBendAllowance(), s.Allowance);
            return fm.InsertSheetMetalEdgeFlange((Edge)edge, (Feature)sketch, options, s.AngleDeg * Rad, (s.RadiusMm ?? 0) / Mm, Position(s.Position), 0,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 0.5, 0, 0, LengthFrom(s.LengthFrom), cba);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertSheetMetalMiterFlange with the profile sketch selected and the edges appended (CADapter's order): the
    /// default relief, the default radius unless one is given, the rip gap, no offsets.
    /// </summary>
    public object? MiterFlange(object sketch, IReadOnlyList<object> edges, MiterFlangeSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(sketch, append: false, mark: 0);
            foreach (var edge in edges) Pick(edge, append: true, mark: 0);
            var fm = Doc.FeatureManager;
            var cba = s.Allowance == null ? null : Write(fm.CreateCustomBendAllowance(), s.Allowance);
            return fm.InsertSheetMetalMiterFlange(s.RadiusMm == null, (s.RadiusMm ?? 0) / Mm, s.GapMm / Mm, true, true, 0.5, 0, 0,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, false, Position(s.Position), 0, 0, cba);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    static int HemType(HemKind kind) => kind switch
    {
        HemKind.Open => (int)swHemTypes_e.swHemTypeOpen,
        HemKind.Teardrop => (int)swHemTypes_e.swHemTypeTearDrop,
        HemKind.Rolled => (int)swHemTypes_e.swHemTypeRolled,
        _ => (int)swHemTypes_e.swHemTypeClosed,
    };

    /// <summary>InsertSheetMetalHem2 on the selected edges, with CADapter's relief arguments (rectangular, ratio 0.5).</summary>
    public object? Hem(IReadOnlyList<object> edges, HemSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            for (int i = 0; i < edges.Count; i++) Pick(edges[i], append: i > 0, mark: 0);
            var fm = Doc.FeatureManager;
            var cba = s.Allowance == null ? null : Write(fm.CreateCustomBendAllowance(), s.Allowance);
            int position = s.Inside ? (int)swHemPositionTypes_e.swHemPositionTypeInside : (int)swHemPositionTypes_e.swHemPositionTypeOutside;
            return fm.InsertSheetMetalHem2(HemType(s.Kind), position, s.Reverse, s.LengthMm / Mm, s.GapMm / Mm, s.AngleDeg * Rad, s.RadiusMm / Mm, s.MiterGapMm / Mm, cba,
                true, (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 1, true, 0.5, 0, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }
}
