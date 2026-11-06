using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Phase 3.D's features, through IFeatureManager and IModelDoc2 (design 371, Features). UI thread only. As in
/// SolidWorksFeatures.cs, each call selects what SOLIDWORKS reads with its marks, makes the feature and clears the
/// selection; millimetres and degrees become metres and radians here. Covered by the parts live suite, not unit tests.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    /// <summary>
    /// InsertProtrusionSwept4 or InsertCutSwept5 with the profile selected with mark 1, the path with mark 4 and each
    /// guide with mark 2: the profile follows the path (twist control 0) without tangency or thin wall.
    /// </summary>
    public object? Sweep(object profile, SweepSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(profile, append: false, mark: 1);
            Pick(s.Path, append: true, mark: 4);
            foreach (var guide in s.Guides) Pick(guide, append: true, mark: 2);
            var fm = Doc.FeatureManager;
            const int follow = (int)swTwistControlType_e.swTwistControlFollowPath;
            const int none = (int)swTangencyType_e.swTangencyNone;
            return s.Cut
                ? fm.InsertCutSwept5(false, true, follow, false, false, none, none, false, 0, 0, 0, none, true, true, 0, true,
                    false, false, false, false, 0, 0)
                : fm.InsertProtrusionSwept4(false, true, follow, false, false, none, none, false, 0, 0, 0, none, s.Merge, true, true, 0, true,
                    false, 0, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertProtrusionBlend2 or InsertCutBlend with the profiles selected in order with mark 1 and the guides with
    /// mark 2: an open loft, no start or end tangency, no thin wall.
    /// </summary>
    public object? Loft(LoftSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            for (int i = 0; i < s.Profiles.Count; i++) Pick(s.Profiles[i], append: i > 0, mark: 1);
            foreach (var guide in s.Guides) Pick(guide, append: true, mark: 2);
            var fm = Doc.FeatureManager;
            const short none = (short)swTangencyType_e.swTangencyNone;
            return s.Cut
                ? fm.InsertCutBlend(false, true, false, 1, none, none, false, 0, 0, 0, true, true)
                : fm.InsertProtrusionBlend2(false, true, false, 1, none, none, 1, 1, true, true, false, 0, 0, 0, s.Merge, true, true, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertRib with the sketch selected (mark 0). It returns nothing, so the new rib is the Rib feature that was not in
    /// the tree before.
    /// </summary>
    public object? Rib(object sketch, RibSpec s)
    {
        return NewFeature("Rib", () =>
        {
            Pick(sketch, append: false, mark: 0);
            Doc.FeatureManager.InsertRib(s.Side == RibSide.Both, s.Side == RibSide.OtherSide, s.ThicknessMm / Mm, 0, s.FlipMaterial,
                s.DraftDeg > 0, s.DraftOutward, s.DraftDeg * Rad, s.NormalToSketch, false);
        });
    }

    /// <summary>Runs <paramref name="make"/> with the selection cleared before and after; the feature of SOLIDWORKS type <paramref name="type"/> it added, or null.</summary>
    IFeature? NewFeature(string type, Action make)
    {
        var before = new HashSet<string>(AllFeatures().Where(f => f.GetTypeName2() == type).Select(f => f.Name), StringComparer.Ordinal);
        Doc.ClearSelection2(true);
        try
        {
            make();
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
        return AllFeatures().FirstOrDefault(f => f.GetTypeName2() == type && !before.Contains(f.Name));
    }

    /// <summary>IModelDoc2.InsertFeatureShell with the faces to remove selected (mark 0); it returns nothing, so the new shell is the Shell feature that was not in the tree before.</summary>
    public object? Shell(IReadOnlyList<object> facesToRemove, double thicknessMm, bool outward)
    {
        return NewFeature("Shell", () =>
        {
            for (int i = 0; i < facesToRemove.Count; i++) Pick(facesToRemove[i], append: i > 0, mark: 0);
            Doc.InsertFeatureShell(thicknessMm / Mm, outward);
        });
    }

    /// <summary>
    /// InsertMultiFaceDraft, a neutral-plane draft without propagation, with the neutral plane selected with mark 1 and
    /// the faces with mark 2.
    /// </summary>
    public object? Draft(DraftSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(s.Neutral, append: false, mark: 1);
            foreach (var face in s.Faces) Pick(face, append: true, mark: 2);
            return Doc.FeatureManager.InsertMultiFaceDraft(s.AngleDeg * Rad, s.Reverse, false,
                (int)swDraftFacePropagationType_e.swFacePropNone, false, false);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }
}
