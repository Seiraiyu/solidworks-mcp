using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.AddIn;

/// <summary>
/// The feature tree as an agent reads it, from the features as SOLIDWORKS walks them (IModelDoc2.FirstFeature and
/// IFeature.GetNextFeature, each with its sub-features). That walk starts with SOLIDWORKS' own folders (Comments,
/// History, Solid Bodies, Material and so on), which every template puts before its planes, and it also returns a
/// sketch an extrude absorbed on its own, just before the extrude. The tree starts at the first reference plane and
/// lists an absorbed sketch once, under the feature that uses it (3.A live run).
/// </summary>
public static class FeatureTree
{
    public static IReadOnlyList<FeatureRecord> Arrange(IReadOnlyList<(FeatureRecord Top, IReadOnlyList<FeatureRecord> Subs)> walked)
    {
        int first = 0;
        for (int i = 0; i < walked.Count; i++)
        {
            if (walked[i].Top.Type == "RefPlane")
            {
                first = i;
                break;
            }
        }
        var absorbed = new HashSet<string>(walked.SelectMany(w => w.Subs).Select(s => s.Name), StringComparer.Ordinal);
        var tree = new List<FeatureRecord>();
        for (int i = first; i < walked.Count; i++)
        {
            if (absorbed.Contains(walked[i].Top.Name)) continue;
            tree.Add(walked[i].Top);
            tree.AddRange(walked[i].Subs);
        }
        return tree;
    }
}
