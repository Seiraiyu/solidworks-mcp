using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.AddIn;

/// <summary>
/// Puts an assembly's components in FeatureManager tree order. IComponent2.GetChildren does not promise it: the 4.A live
/// run (round 5) saw post-1 listed before base-1, which was inserted first and is first in the tree.
/// </summary>
public static class TreeOrder
{
    /// <summary>
    /// <paramref name="children"/> in the order their names come in <paramref name="tree"/> (case-insensitive); those the
    /// tree does not name follow, in the order they came.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(IReadOnlyList<T> children, IReadOnlyList<string> tree, Func<T, string> name)
    {
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tree.Count; i++)
        {
            if (!position.ContainsKey(tree[i])) position[tree[i]] = i;
        }
        return children
            .Select((c, i) => (Child: c, Index: i, Tree: position.TryGetValue(name(c), out int p) ? p : int.MaxValue))
            .OrderBy(x => x.Tree)
            .ThenBy(x => x.Index)
            .Select(x => x.Child)
            .ToList();
    }
}
