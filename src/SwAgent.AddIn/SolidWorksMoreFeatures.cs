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
}
