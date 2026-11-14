using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.AddIn.Core;

/// <summary>A balloon as the check reads it: its name, its item number, and the components of what it is attached to.</summary>
public sealed class BalloonFacts
{
    public BalloonFacts(string name, string? itemNumber, IReadOnlyList<string> components)
    {
        Name = name;
        ItemNumber = itemNumber;
        Components = components;
    }

    public string Name { get; }

    public string? ItemNumber { get; }

    public IReadOnlyList<string> Components { get; }

    /// <summary>What it is attached to when no component reads from it (the interfaces SOLIDWORKS gave), for the refusal to say; null otherwise.</summary>
    public string? AttachedNote { get; set; }
}

/// <summary>
/// How the balloons of an assembly view are checked (design 611, Assembly drawings: one balloon per visible component,
/// each attached to a component of the view, and each item number one the bill of materials lists). Components are
/// grouped into items by file and configuration, as a bill of materials groups them, and SOLIDWORKS balloons one
/// instance of each item (Auto Balloon's ignore-multiple-instances): every item the view shows must have a balloon, no
/// item more than one made by this call, and each balloon must be attached to exactly one component the view shows.
/// With a bill of materials each balloon's number is its item's row's; without one, an item's balloons share a number
/// and two items never do.
/// </summary>
public static class BalloonCheck
{
    /// <summary>
    /// Null when the balloons hold; otherwise (check, balloon, expected, measured) for the first that does not.
    /// <paramref name="itemOf"/> gives a visible component's item (BomRows.Key of its file and configuration);
    /// <paramref name="bomNumbers"/> each item's number in the bill of materials, or null when the view has none.
    /// </summary>
    public static (string Check, string Balloon, string Expected, string Measured)? Mismatch(
        IReadOnlyList<string> visible, Func<string, string> itemOf, IReadOnlyList<BalloonFacts> made, IReadOnlyList<BalloonFacts> already,
        IReadOnlyDictionary<string, string>? bomNumbers)
    {
        var shown = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        foreach (var b in made)
        {
            if (b.Components.Count != 1 || !shown.Contains(b.Components[0]))
                return ("balloon_component", b.Name, "attached to one component the view shows", b.Components.Count == 0 ? (b.AttachedNote == null ? "attached to none" : $"attached to none ({b.AttachedNote})") : string.Join(", ", b.Components));
            if (string.IsNullOrWhiteSpace(b.ItemNumber))
                return ("balloon_item", b.Name, "an item number", "none");
        }
        var all = made.Concat(already.Where(a => a.Components.Count == 1 && shown.Contains(a.Components[0]))).ToList();
        foreach (var group in made.GroupBy(b => itemOf(b.Components[0]), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
                return ("balloon_count", group.First().Name, "one balloon for each item", $"{group.Count()} for {group.First().Components[0]}'s item ({string.Join(", ", group.Select(b => b.Name))})");
        }
        foreach (var item in visible.GroupBy(itemOf, StringComparer.OrdinalIgnoreCase))
        {
            if (!all.Any(b => string.Equals(itemOf(b.Components[0]), item.Key, StringComparison.OrdinalIgnoreCase)))
                return ("balloon_count", "", $"a balloon on {string.Join(" or ", item)}", "none");
        }
        foreach (var b in all)
        {
            string item = itemOf(b.Components[0]);
            string number = b.ItemNumber!.Trim();
            if (bomNumbers != null)
            {
                if (!bomNumbers.TryGetValue(item, out var row))
                    return ("balloon_item", b.Name, $"an item the bill of materials lists for {b.Components[0]}", "the bill lists no row for it");
                if (!string.Equals(row.Trim(), number, StringComparison.OrdinalIgnoreCase))
                    return ("balloon_item", b.Name, $"item {row} for {b.Components[0]}, as the bill of materials numbers it", $"item {number}");
                continue;
            }
            var other = all.FirstOrDefault(o => !ReferenceEquals(o, b) && !string.IsNullOrWhiteSpace(o.ItemNumber)
                && string.Equals(itemOf(o.Components[0]), item, StringComparison.OrdinalIgnoreCase) != string.Equals(o.ItemNumber!.Trim(), number, StringComparison.OrdinalIgnoreCase));
            if (other != null)
                return ("balloon_item", b.Name, "one number per item, a different number for each item", $"{b.Name} on {b.Components[0]} is {number}, {other.Name} on {other.Components[0]} is {other.ItemNumber!.Trim()}");
        }
        return null;
    }
}
