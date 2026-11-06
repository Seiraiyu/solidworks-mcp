using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Features, through IFeatureManager (design 371, Features). UI thread only. Each call selects what SOLIDWORKS' feature
/// API needs, with the marks it reads, makes the feature and clears the selection. Millimetres and degrees become
/// metres and radians here and nowhere else. Covered by the parts live suite, not by unit tests.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    const double Rad = Math.PI / 180;

    public void Delete(object feature, bool children, bool absorbed)
    {
        var f = (IFeature)feature;
        Doc.ClearSelection2(true);
        if (!f.Select2(false, 0)) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select {f.Name} to delete it");
        int options = (children ? (int)swDeleteSelectionOptions_e.swDelete_Children : 0) | (absorbed ? (int)swDeleteSelectionOptions_e.swDelete_Absorbed : 0);
        bool deleted = Doc.Extension.DeleteSelection2(options);
        Doc.ClearSelection2(true);
        if (!deleted) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not delete {f.Name}");
    }

    /// <summary>
    /// FeatureExtrusion3 or FeatureCut4 (FeatureExtrusionThin2 or FeatureCutThin2 for a thin wall), with the sketch
    /// selected (mark 0), the first direction's up-to entity with mark 1 and the second's with mark 2 (measured by the
    /// parts live suite). One direction unless a second is given (Sd false, its end in T2, D2, Dchk2 and Dang2).
    /// </summary>
    public object? Extrude(object sketch, ExtrudeSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(sketch, append: false, mark: 0);
            if (s.UpTo != null) Pick(s.UpTo, append: true, mark: 1);
            var second = s.Direction2;
            if (second?.UpTo != null) Pick(second.UpTo, append: true, mark: 2);
            bool sd = second == null;
            int end = EndCondition(s.End);
            int end2 = second == null ? (int)swEndConditions_e.swEndCondBlind : EndCondition(second.End);
            double depth = s.DepthMm / Mm;
            double depth2 = second == null ? depth : second.DepthMm / Mm;
            bool draft = s.DraftDeg > 0, draft2 = second != null && second.DraftDeg > 0;
            double angle = s.DraftDeg * Rad, angle2 = second == null ? angle : second.DraftDeg * Rad;
            const int start = (int)swStartConditions_e.swStartSketchPlane;
            var fm = Doc.FeatureManager;
            if (s.Thin != null)
            {
                int type = s.Thin.Side switch
                {
                    ThinSide.OtherSide => (int)swThinWallType_e.swThinWallOppDirection,
                    ThinSide.MidPlane => (int)swThinWallType_e.swThinWallMidPlane,
                    ThinSide.TwoSides => (int)swThinWallType_e.swThinWallTwoDirection,
                    _ => (int)swThinWallType_e.swThinWallOneDirection,
                };
                double t1 = s.Thin.ThicknessMm / Mm, t2 = (s.Thin.Side == ThinSide.TwoSides ? s.Thin.Thickness2Mm : s.Thin.ThicknessMm) / Mm;
                return s.Cut
                    ? fm.FeatureCutThin2(sd, false, s.Reverse, end, end2, depth, depth2, draft, draft2, s.DraftOutward, s.DraftOutward, angle, angle2,
                        false, false, false, false, t1, t2, 0, type, 0, false, 0, true, true, start, 0, false)
                    : fm.FeatureExtrusionThin2(sd, false, s.Reverse, end, end2, depth, depth2, draft, draft2, s.DraftOutward, s.DraftOutward, angle, angle2,
                        false, false, false, false, s.Merge, t1, t2, 0, type, 0, false, 0, true, true, start, 0, false);
            }
            return s.Cut
                ? fm.FeatureCut4(sd, false, s.Reverse, end, end2, depth, depth2, draft, draft2, s.DraftOutward, s.DraftOutward, angle, angle2,
                    false, false, false, false, false, true, true, true, true, false, start, 0, false, false)
                : fm.FeatureExtrusion3(sd, false, s.Reverse, end, end2, depth, depth2, draft, draft2, s.DraftOutward, s.DraftOutward, angle, angle2,
                    false, false, false, false, s.Merge, true, true, start, 0, false);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    static int EndCondition(ExtrudeEnd end) => end switch
    {
        ExtrudeEnd.ThroughAll => (int)swEndConditions_e.swEndCondThroughAll,
        ExtrudeEnd.UpToNext => (int)swEndConditions_e.swEndCondUpToNext,
        ExtrudeEnd.UpToVertex => (int)swEndConditions_e.swEndCondUpToVertex,
        ExtrudeEnd.UpToSurface => (int)swEndConditions_e.swEndCondUpToSurface,
        ExtrudeEnd.MidPlane => (int)swEndConditions_e.swEndCondMidPlane,
        _ => (int)swEndConditions_e.swEndCondBlind,
    };

    /// <summary>
    /// FeatureRevolve2 with the sketch (mark 0) and the axis selected: with mark 4, and again with mark 16 when SOLIDWORKS
    /// made nothing with 4 (which mark FeatureRevolve2 reads is measured by the parts live suite).
    /// </summary>
    public object? Revolve(object sketch, RevolveSpec s)
    {
        foreach (int mark in new[] { 4, 16 })
        {
            Doc.ClearSelection2(true);
            try
            {
                Pick(sketch, append: false, mark: 0);
                Pick(s.Axis, append: true, mark: mark);
                var made = Doc.FeatureManager.FeatureRevolve2(true, true, false, s.Cut, s.Reverse, false,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, s.AngleDeg * Rad, 0,
                    false, false, 0, 0, 0, 0, 0, s.Merge, true, true);
                if (made != null) return made;
            }
            finally
            {
                Doc.ClearSelection2(true);
            }
        }
        return null;
    }

    /// <summary>
    /// FeatureFillet3 with the edges and faces selected with mark 1: a simple fillet of one radius, propagating along
    /// tangent edges and keeping the features it meets (options 195, as the Fillet PropertyManager's defaults).
    /// </summary>
    public object? Fillet(IReadOnlyList<object> edgesAndFaces, double radiusMm)
    {
        Doc.ClearSelection2(true);
        try
        {
            bool first = true;
            foreach (var e in edgesAndFaces)
            {
                Pick(e, append: !first, mark: 1);
                first = false;
            }
            int options = (int)swFeatureFilletOptions_e.swFeatureFilletPropagate | (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius
                | (int)swFeatureFilletOptions_e.swFeatureFilletAttachEdges | (int)swFeatureFilletOptions_e.swFeatureFilletKeepFeatures;
            return Doc.FeatureManager.FeatureFillet3(options, radiusMm / Mm, 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_Simple, 0, 0,
                null, null, null, null, null, null, null);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>InsertFeatureChamfer with the edges selected (mark 0), propagating along tangent edges: angle-distance, or distance-distance.</summary>
    public object? Chamfer(IReadOnlyList<object> edges, ChamferSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            bool first = true;
            foreach (var e in edges)
            {
                Pick(e, append: !first, mark: 0);
                first = false;
            }
            bool twoDistances = s.Distance2Mm != null;
            return Doc.FeatureManager.InsertFeatureChamfer((int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation,
                twoDistances ? (int)swChamferType_e.swChamferDistanceDistance : (int)swChamferType_e.swChamferAngleDistance,
                s.DistanceMm / Mm, twoDistances ? 0 : s.AngleDeg * Rad, twoDistances ? s.Distance2Mm!.Value / Mm : 0, 0, 0, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertRefPlane with the references selected with marks 0, 1 and 2, each with its constraint: a distance from the
    /// first (offset), an angle to the first about the second, coincident with three points, parallel to the first
    /// through the second, or perpendicular to a curve through a point on it.
    /// </summary>
    public object? Plane(PlaneSpec s)
    {
        const int Parallel = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Parallel;
        const int Perpendicular = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Perpendicular;
        const int Coincident = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident;
        const int Distance = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
        const int Angle = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle;
        int flip = s.Flip ? (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip : 0;
        var (c, v) = s.Mode switch
        {
            PlaneMode.Offset => (new[] { Distance | flip, 0, 0 }, new[] { s.DistanceMm / Mm, 0, 0 }),
            PlaneMode.Angle => (new[] { Angle | flip, Coincident, 0 }, new[] { s.AngleDeg * Rad, 0, 0 }),
            PlaneMode.ThreePoints => (new[] { Coincident, Coincident, Coincident }, new[] { 0.0, 0, 0 }),
            PlaneMode.ParallelThroughPoint => (new[] { Parallel, Coincident, 0 }, new[] { 0.0, 0, 0 }),
            _ => (new[] { Perpendicular, Coincident, 0 }, new[] { 0.0, 0, 0 }),
        };
        Doc.ClearSelection2(true);
        try
        {
            for (int i = 0; i < s.References.Count; i++) Pick(s.References[i], append: i > 0, mark: i);
            return Doc.FeatureManager.InsertRefPlane(c[0], v[0], c[1], v[1], c[2], v[2]);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    public IReadOnlyList<object> FacesOf(object feature) =>
        ((IFeature)feature).GetFaces() is object[] faces ? faces.Where(f => f != null).ToList() : new List<object>();

    /// <summary>Selects a feature, plane, face, edge or vertex with <paramref name="mark"/>; ApiError when SOLIDWORKS refuses.</summary>
    void Pick(object entity, bool append, int mark)
    {
        bool selected;
        switch (entity)
        {
            case IFeature feature:
                selected = feature.Select2(append, mark);
                break;
            case IEntity e:
                selected = e.Select4(append, Marked(mark));
                break;
            case ISketchSegment segment:
                selected = segment.Select4(append, Marked(mark));
                break;
            default:
                selected = false;
                break;
        }
        if (!selected) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS did not select the {Describe(entity).Kind.ToString().ToLowerInvariant()} a feature needs");
    }

    SelectData Marked(int mark)
    {
        var data = ((ISelectionMgr)Doc.SelectionManager).CreateSelectData();
        data.Mark = mark;
        return data;
    }
}