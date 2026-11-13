using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>One way SOLIDWORKS may take an explode step: its sense along the direction (and about the axis), and where each of the step's components must then be.</summary>
public sealed class ExplodeReading
{
    public ExplodeReading(string sense, string? turnSense, IReadOnlyDictionary<string, Transform> places)
    {
        Sense = sense;
        TurnSense = turnSense;
        Places = places;
    }

    public string Sense { get; }

    public string? TurnSense { get; }

    /// <summary>Where each component the step names must be after it, by name.</summary>
    public IReadOnlyDictionary<string, Transform> Places { get; }
}

/// <summary>
/// Where an explode step puts its components (design 455, Exploded views; plan 4.E), worked out without SOLIDWORKS:
/// each is moved distance along the direction from where the previous steps left it, then, with an axis, turned by the
/// angle about that axis line where it is in the assembly. SOLIDWORKS chooses which way along a direction and about an
/// axis it goes, so a step has two readings (four with a turn); its components must all fit the same one, within 0.001
/// mm and 0.01 degrees (design 455's mate tolerances), and no other component may move.
/// </summary>
public static class ExplodeGeometry
{
    public const double OriginToleranceMm = 0.001;

    public const double TurnToleranceDeg = 0.01;

    public static IReadOnlyList<ExplodeReading> Step(IReadOnlyDictionary<string, Transform> before, IReadOnlyList<string> components,
        Vector3 direction, double distanceMm, (Point3 Through, Vector3 Axis)? axis, double angleDeg)
    {
        var unit = PatternGeometry.Unit(direction);
        var readings = new List<ExplodeReading>();
        foreach (int s in new[] { 1, -1 })
        {
            var by = new Vector3(unit.X * distanceMm * s, unit.Y * distanceMm * s, unit.Z * distanceMm * s);
            foreach (int t in axis == null ? new[] { 0 } : new[] { 1, -1 })
            {
                var places = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in components)
                {
                    var moved = PatternGeometry.Shift(before[name], by);
                    if (axis is (Point3 through, Vector3 line)) moved = PatternGeometry.Turn(moved, through, line, angleDeg * t);
                    places[name] = moved;
                }
                readings.Add(new ExplodeReading(Sense(s), axis == null ? null : Sense(t), places));
            }
        }
        return readings;
    }

    /// <summary>
    /// The step's components paired with where they should be, under the reading they fit best. verification_failed,
    /// with field /steps/&lt;index&gt;, when one is off or turned, or when a component the step does not name moved.
    /// </summary>
    public static (ExplodeReading Reading, List<ExplodedComponent> Components) Match(int index, IReadOnlyList<ExplodeReading> readings,
        IReadOnlyDictionary<string, Transform> before, IReadOnlyDictionary<string, Transform> after)
    {
        string field = $"/steps/{index}";
        (ExplodeReading Reading, List<ExplodedComponent> List, double Worst)? best = null;
        foreach (var reading in readings)
        {
            var list = reading.Places.Select(p => Compare(index, p.Key, before[p.Key], p.Value, after)).ToList();
            double worst = list.Count == 0 ? 0 : list.Max(c => Math.Max(c.OffsetMm / OriginToleranceMm, c.TurnDeg / TurnToleranceDeg));
            if (best == null || worst < best.Value.Worst) best = (reading, list, worst);
        }
        var (chosen, components, _) = best!.Value;
        var off = components.FirstOrDefault(c => c.OffsetMm > OriginToleranceMm);
        if (off != null)
        {
            throw Failed("explode_offset_mm", field, off.Name, 0.0, off.OffsetMm,
                $"step {index + 1} left {off.Name} at {off.Actual}, {Number(off.OffsetMm)} mm from {off.Expected} where it should be");
        }
        var turned = components.FirstOrDefault(c => c.TurnDeg > TurnToleranceDeg);
        if (turned != null)
        {
            throw Failed("explode_turn_deg", field, turned.Name, 0.0, turned.TurnDeg,
                $"step {index + 1} left {turned.Name} turned {Number(turned.TurnDeg)} degrees from how it should be");
        }
        foreach (var other in after.Where(a => !chosen.Places.ContainsKey(a.Key) && before.ContainsKey(a.Key)))
        {
            double moved = PatternGeometry.Distance(before[other.Key].Origin, other.Value.Origin);
            if (moved > OriginToleranceMm || PatternGeometry.TurnDeg(before[other.Key], other.Value) > TurnToleranceDeg)
            {
                throw Failed("explode_unmoved_mm", field, other.Key, 0.0, moved,
                    $"step {index + 1} moved {other.Key} {Number(moved)} mm, though it does not name it");
            }
        }
        return (chosen, components);
    }

    /// <summary>Every component is where it was collapsed: verification_failed naming the first that is not.</summary>
    public static void Back(IReadOnlyDictionary<string, Transform> collapsed, IReadOnlyDictionary<string, Transform> now)
    {
        foreach (var c in collapsed)
        {
            if (!now.TryGetValue(c.Key, out var at)) continue;
            double moved = PatternGeometry.Distance(c.Value.Origin, at.Origin);
            if (moved > OriginToleranceMm || PatternGeometry.TurnDeg(c.Value, at) > TurnToleranceDeg)
            {
                throw Failed("explode_collapsed", "/steps", c.Key, 0.0, moved,
                    $"collapsed again, {c.Key} is at {at.Origin}, {Number(moved)} mm from {c.Value.Origin} where it was");
            }
        }
    }

    static ExplodedComponent Compare(int index, string name, Transform before, Transform expected, IReadOnlyDictionary<string, Transform> after)
    {
        if (!after.TryGetValue(name, out var actual))
            throw Failed("explode_component", $"/steps/{index}", name, name, null, $"{name} is not in the assembly after step {index + 1}");
        return new ExplodedComponent
        {
            Name = name, Before = before.Origin, Expected = expected.Origin, Actual = actual.Origin,
            OffsetMm = PatternGeometry.Distance(expected.Origin, actual.Origin),
            TurnDeg = PatternGeometry.TurnDeg(expected, actual),
        };
    }

    static string Sense(int sign) => sign > 0 ? "along" : "against";

    static string Number(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    static SwAgentException Failed(string check, string field, string component, object? expected, object? measured, string message) =>
        new SwAgentException(ErrorCode.VerificationFailed, message, new JsonObject
        {
            ["check"] = check, ["field"] = field, ["component"] = component, ["expected"] = expected, ["measured"] = measured,
        });
}
