using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the pattern and mirror operations share: their seeds, the seeds' volume, and the checks on what they made.</summary>
public static class PatternFacts
{
    public const int MaxInstances = 500;

    /// <summary>The seeds: features (not sketches or reference geometry), each once and not suppressed; fields /features/i.</summary>
    public static List<(object Feature, string Name)> Seeds(ReferenceService references, ISwDocument doc, IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) throw FeatureFacts.Invalid("features", "name at least one feature to repeat");
        var seeds = FeatureFacts.Entities(references, doc, texts, "features", ReferenceKind.Feature)
            .Select(f => (Feature: f, Name: doc.Describe(f).Name ?? "?")).ToList();
        for (int i = 0; i < seeds.Count; i++)
        {
            if (FeatureFacts.IsSuppressed(doc, seeds[i].Name))
                throw FeatureFacts.Invalid($"/features/{i}", $"/features/{i} ({seeds[i].Name}) is suppressed, so there is nothing of it to repeat; unsuppress it first");
        }
        return seeds;
    }

    /// <summary>
    /// What a pattern repeats: features (the seeds) or faces, exactly one of them; invalid_argument naming features
    /// when both or neither are given. Faces are resolved as /faces/i.
    /// </summary>
    public static (List<(object Feature, string Name)> Seeds, List<object> Faces) Targets(ReferenceService references, ISwDocument doc, IReadOnlyList<string>? features, IReadOnlyList<string>? faces)
    {
        bool f = (features?.Count ?? 0) > 0, g = (faces?.Count ?? 0) > 0;
        if (f == g) throw FeatureFacts.Invalid("features", "a pattern repeats features or faces: give one of them, not both");
        return f
            ? (Seeds(references, doc, features!), new List<object>())
            : (new List<(object, string)>(), FeatureFacts.Entities(references, doc, faces, "faces", ReferenceKind.Face));
    }

    /// <summary>An instance count from 2 to <see cref="MaxInstances"/>.</summary>
    public static int Count(int? value, string field)
    {
        if (value == null) throw FeatureFacts.Invalid(field, $"{field} is needed: how many instances, the seed included");
        if (value < 2 || value > MaxInstances) throw FeatureFacts.Invalid(field, $"{field} must be from 2 to {MaxInstances}, not {value}");
        return value.Value;
    }

    /// <summary>
    /// The volume the seeds add (or, for cuts, take away: negative), mm³: the seeds are suppressed, the part rebuilt and
    /// measured, and the seeds unsuppressed again, before anything is made. Null when a seed has features built on it
    /// (they would go with it) or SOLIDWORKS will not suppress one. operation_failed when the part does not come back
    /// to <paramref name="volumeBefore"/>.
    /// </summary>
    public static double? SeedVolume(ISwDocument doc, IReadOnlyList<(object Feature, string Name)> seeds, double volumeBefore)
    {
        if (seeds.Any(s => doc.ChildrenOf(s.Feature).Count > 0)) return null;
        var suppressed = new List<object>();
        double without;
        try
        {
            foreach (var s in seeds)
            {
                if (!doc.Suppress(s.Feature, true)) return null;
                suppressed.Add(s.Feature);
            }
            doc.Rebuild(false);
            int bodies = doc.SolidBodyCount();
            without = bodies == 0 ? 0 : doc.Mass()?.VolumeMm3 ?? 0;
        }
        finally
        {
            foreach (var f in suppressed) doc.Suppress(f, false);
            if (suppressed.Count > 0) doc.Rebuild(false);
        }
        int back = doc.SolidBodyCount();
        double now = back == 0 ? 0 : doc.Mass()?.VolumeMm3 ?? 0;
        if (Math.Abs(now - volumeBefore) > 1e-6 * Math.Max(1, Math.Abs(volumeBefore)))
        {
            throw new SwAgentException(ErrorCode.OperationFailed,
                FormattableString.Invariant($"measuring the seeds left the part at {now:0.###} mm³, not the {volumeBefore:0.###} mm³ it was; nothing was patterned, and the seeds are unsuppressed"),
                new JsonObject { ["before"] = volumeBefore, ["measured"] = now });
        }
        return volumeBefore - without;
    }

    /// <summary>
    /// The feature is of <paramref name="type"/>, has <paramref name="instances"/> instances as its definition reads back
    /// (not checked when null: a mirror's definition always reads two), and the volume changed by the seeds' volume for
    /// each new instance, or (faces, or seeds not measured) changed at all.
    /// </summary>
    public static void Check(ISwDocument doc, FeatureResult result, string type, string what, int instances, int? readBack, double? seed, double before, double after)
    {
        FeatureFacts.ExpectType(doc, result, type, what);
        if (readBack != null)
        {
            int made = doc.PatternInstances(doc.FeatureByName(result.Feature.Name ?? "") ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} is gone", new JsonObject { ["check"] = "feature_exists" }));
            if (made != readBack)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} has {made} instances, not the {readBack} asked",
                    new JsonObject { ["check"] = "instances", ["expected"] = readBack, ["measured"] = made });
            }
            result.Checks.Add($"{made} instances, the seed included");
        }
        if (seed == null)
        {
            FeatureChecks.VolumeMoved(what, before, after);
            result.Checks.Add(FormattableString.Invariant($"the volume changed: {before:0.###} -> {after:0.###} mm³ (the seeds' own volume is not measured for faces, or seeds that features are built on)"));
            return;
        }
        double expected = seed.Value * (instances - 1);
        string why = FormattableString.Invariant($"{instances - 1} new instances × the seeds' {seed.Value:0.###} mm³");
        FeatureChecks.VolumeChangeIs(what, expected, after - before, why + "; an instance that runs off the part or into another changes it by a different amount");
        result.Checks.Add(FormattableString.Invariant($"the volume changed by {after - before:0.###} mm³, {why}"));
    }
}

public sealed class FeatureLinearPatternOperation : ModelChangeOperation<FeatureLinearPatternRequest, FeatureResult>
{
    double? _seed;

    public FeatureLinearPatternOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureLinearPatternRequest request) => request.Document;

    static readonly ReferenceKind[] Directions = { ReferenceKind.Edge, ReferenceKind.Axis, ReferenceKind.Plane, ReferenceKind.Face };

    protected override FeatureResult Change(ISwDocument doc, FeatureLinearPatternRequest request, ModelTransaction tx, IOperationContext context)
    {
        var references = Services.References;
        var spec = new LinearPatternSpec
        {
            SpacingMm = FeatureFacts.Length(request.SpacingMm, "spacing_mm", "a linear pattern"),
            Count = PatternFacts.Count(request.Count, "count"),
            Reverse = request.Reverse == true,
        };
        if (request.Direction2 != null)
        {
            spec.SpacingMm2 = FeatureFacts.Length(request.Direction2.SpacingMm, "/direction2/spacing_mm", "a linear pattern's second direction");
            spec.Count2 = PatternFacts.Count(request.Direction2.Count, "/direction2/count");
            spec.Reverse2 = request.Direction2.Reverse == true;
        }
        if (spec.Count * spec.Count2 > PatternFacts.MaxInstances)
            throw FeatureFacts.Invalid("count", $"count × /direction2/count is {spec.Count * spec.Count2}, more than {PatternFacts.MaxInstances} instances");
        var (seeds, faces) = PatternFacts.Targets(references, doc, request.Features, request.Faces);
        spec.Direction = Direction(doc, request.Direction, "direction");
        if (request.Direction2 != null) spec.Direction2 = Direction(doc, request.Direction2.Direction, "/direction2/direction");
        spec.Seeds.AddRange(seeds.Select(s => s.Feature));
        spec.Faces.AddRange(faces);
        _seed = faces.Count > 0 ? null : PatternFacts.SeedVolume(doc, seeds, VolumeBefore);
        string of = faces.Count > 0 ? $"of {faces.Count} faces" : $"of {string.Join(", ", seeds.Select(s => s.Name))}";
        var feature = doc.LinearPattern(spec)
            ?? throw FeatureFacts.NotMade(doc, "linear pattern", of, "check that the direction is straight or planar and that the instances land on the part");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    object Direction(ISwDocument doc, string text, string field)
    {
        var (entity, kind) = Services.References.Resolve(doc, text, field, Directions);
        var info = doc.Describe(entity);
        if ((kind == ReferenceKind.Edge && info.Geometry != GeometryType.Line) || (kind == ReferenceKind.Face && info.Geometry != GeometryType.Plane))
            throw FeatureFacts.Invalid(field, $"{field}: a pattern runs along a straight edge, an axis or a plane's normal, and this is a {WireNames.ForEnum(info.Geometry)}");
        return entity;
    }

    protected override void Check(ISwDocument doc, FeatureLinearPatternRequest request, FeatureResult result)
    {
        int instances = request.Count!.Value * (request.Direction2?.Count ?? 1);
        PatternFacts.Check(doc, result, "LPattern", "linear pattern", instances, instances, _seed, VolumeBefore, Volume(doc, doc.SolidBodyCount()));
    }
}
