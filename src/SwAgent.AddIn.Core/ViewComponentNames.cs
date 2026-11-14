using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The names of a drawing view's components as the assembly names them. SOLIDWORKS 2026 SP4 names the components
/// IView.GetVisibleComponents gives from the view's own root component (pinblock-2/pin-1, measured in the 5.D live run),
/// where the assembly (assembly.components, model references) names them pin-1. The root is the first path segment every
/// one of the view's names shares; a name under it loses it, and any other name is left as it is.
/// </summary>
public static class ViewComponentNames
{
    /// <summary>The root segment every name starts with (root/...), or null when they do not all share one.</summary>
    public static string? Root(IEnumerable<string> names)
    {
        string? root = null;
        bool any = false;
        foreach (var name in names)
        {
            any = true;
            int slash = name.IndexOf('/');
            if (slash <= 0) return null;
            string first = name.Substring(0, slash);
            if (root == null) root = first;
            else if (!string.Equals(root, first, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return any ? root : null;
    }

    /// <summary><paramref name="name"/> without the view's <paramref name="root"/> segment, when it has it.</summary>
    public static string Unrooted(string name, string? root)
    {
        if (root == null || name.Length <= root.Length + 1) return name;
        return name.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? name.Substring(root.Length + 1) : name;
    }

    /// <summary>Each of <paramref name="names"/> without the root they share.</summary>
    public static IReadOnlyList<string> Unrooted(IReadOnlyList<string> names)
    {
        string? root = Root(names);
        return names.Select(n => Unrooted(n, root)).ToList();
    }
}
