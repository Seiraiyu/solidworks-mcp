using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.AddIn.Core;

/// <summary>
/// A component pattern's seeds, read from the pattern feature's parents when SOLIDWORKS gives none in its definition
/// (2026 SP4: SeedComponentArray reads back empty; plan 4.D, Known limit; plan 4.E Task 9). A parent is a seed when some
/// instance is a copy of it (the same file under another name), except a parent that only gave the pattern its direction
/// or axis: that one is left out, unless leaving it out would leave no seed for its file (a seed patterned along its own
/// edge). Names keep the parents' order.
/// </summary>
public static class PatternSeedFilter
{
    public static List<string> FromParents(IReadOnlyList<(string Name, string? Path)> parents, IReadOnlyList<(string Name, string? Path)> instances,
        IReadOnlyCollection<string> directionOwners)
    {
        bool Copied((string Name, string? Path) p) => p.Path != null && instances.Any(i =>
            !string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(i.Path, p.Path, StringComparison.OrdinalIgnoreCase));
        bool Owner(string name) => directionOwners.Contains(name, StringComparer.OrdinalIgnoreCase);

        var candidates = parents.Where(Copied)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in candidates.GroupBy(c => c.Path!, StringComparer.OrdinalIgnoreCase))
        {
            var seeds = file.Where(c => !Owner(c.Name)).ToList();
            foreach (var s in seeds.Count > 0 ? seeds : file.ToList()) kept.Add(s.Name);
        }
        return candidates.Where(c => kept.Contains(c.Name)).Select(c => c.Name).ToList();
    }
}
