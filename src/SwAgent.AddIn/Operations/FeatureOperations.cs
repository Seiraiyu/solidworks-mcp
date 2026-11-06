using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class FeatureExtrudeOperation : ModelChangeOperation<FeatureExtrudeRequest, FeatureResult>
{
    public FeatureExtrudeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureExtrudeRequest request) => request.Document;

    static string What(FeatureExtrudeRequest request) => request.Mode == FeatureMode.Cut ? "cut extrude" : "boss extrude";

    protected override FeatureResult Change(ISwDocument doc, FeatureExtrudeRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = Spec(request);
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        if (request.UpTo != null) spec.UpTo = UpTo(doc, request.UpTo, spec.End, "up_to");
        if (request.Direction2?.UpTo != null) spec.Direction2!.UpTo = UpTo(doc, request.Direction2.UpTo, spec.Direction2.End, "/direction2/up_to");
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What(request));
        var feature = doc.Extrude(sketch, spec)
            ?? throw FeatureFacts.NotMade(doc, What(request), $"from {profile.Name}", "check that its profile is closed and that the end can be reached");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    object UpTo(ISwDocument doc, string text, ExtrudeEnd end, string field) => end == ExtrudeEnd.UpToVertex
        ? Services.References.Resolve(doc, text, field, ReferenceKind.Vertex, ReferenceKind.Point).Entity
        : Services.References.Resolve(doc, text, field, ReferenceKind.Face, ReferenceKind.Plane).Entity;

    /// <summary>The feature is an extrusion or a cut, the volume moved the right way, and a blind boss on an empty part added exactly area × depth.</summary>
    protected override void Check(ISwDocument doc, FeatureExtrudeRequest request, FeatureResult result)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        // IFeature.GetTypeName2 measured live (3.C): a first boss is Extrusion, a thin boss ExtruThin, a cut extrude ICE,
        // and a second boss (up_to_next onto the body) ICE as well; Boss, Cut and CutThin are SOLIDWORKS' other extrude
        // names. So the type only says it is an extrude; whether it added or removed material is the volume check's.
        var types = request.Thin != null
            ? (cut ? new[] { "CutThin", "ICE" } : new[] { "ExtruThin", "BossThin" })
            : cut ? new[] { "ICE", "Cut" } : new[] { "Extrusion", "Boss", "ICE" };
        FeatureFacts.ExpectType(doc, result, types, What(request));
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureFacts.VolumeChanged(result, What(request), VolumeBefore, after, cut ? -1 : 1);
        var end = request.End ?? ExtrudeEnd.Blind;
        var second = request.Direction2;
        bool blindSecond = second == null || ((second.End ?? ExtrudeEnd.Blind) == ExtrudeEnd.Blind && second.DraftDeg == null);
        bool exact = !cut && (end == ExtrudeEnd.Blind || end == ExtrudeEnd.MidPlane) && (request.DraftDeg ?? 0) == 0 && request.Thin == null && blindSecond && BodiesBefore == 0;
        if (exact)
        {
            var (sketch, _) = Services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
            var region = FeatureFacts.Region(doc.ReadSketch(sketch));
            if (region != null)
            {
                double depth = request.DepthMm!.Value + (second?.DepthMm ?? 0);
                string why = FormattableString.Invariant($"{region.AreaMm2:0.###} mm² × {depth:0.###} mm");
                FeatureChecks.VolumeChangeIs(What(request), region.AreaMm2 * depth, after - VolumeBefore, why);
                result.Checks.Add(FormattableString.Invariant($"the volume grew by {after - VolumeBefore:0.###} mm³, the profile's {why}"));
            }
        }
        FeatureFacts.MadeFaces(result, What(request));
    }

    /// <summary>The request as SOLIDWORKS takes it, every field checked: invalid_argument names the first that is wrong.</summary>
    static ExtrudeSpec Spec(FeatureExtrudeRequest r)
    {
        var e = r.End ?? ExtrudeEnd.Blind;
        bool depthEnd = e == ExtrudeEnd.Blind || e == ExtrudeEnd.MidPlane;
        bool upToEnd = e == ExtrudeEnd.UpToVertex || e == ExtrudeEnd.UpToSurface;
        string end = WireNames.ForEnum(e);
        if (depthEnd) FeatureFacts.Length(r.DepthMm, "depth_mm", $"a {end} extrude");
        else if (r.DepthMm != null) throw FeatureFacts.Invalid("depth_mm", $"depth_mm is for blind and mid_plane only, and this extrude ends {end}");
        if (upToEnd && r.UpTo == null) throw FeatureFacts.Invalid("up_to", $"an extrude that ends {end} needs up_to: the {(e == ExtrudeEnd.UpToVertex ? "vertex or point" : "face or plane")} it ends at");
        if (!upToEnd && r.UpTo != null) throw FeatureFacts.Invalid("up_to", $"up_to is for up_to_vertex and up_to_surface only, and this extrude ends {end}");
        if (e == ExtrudeEnd.MidPlane && r.Reverse == true) throw FeatureFacts.Invalid("reverse", "a mid_plane extrude goes both ways, so it cannot be reversed");
        if (r.DraftDeg != null && !(r.DraftDeg > 0 && r.DraftDeg < 90)) throw FeatureFacts.Invalid("draft_deg", "draft_deg must be more than 0 and less than 90 degrees");
        if (r.Mode == FeatureMode.Cut && r.Merge != null) throw FeatureFacts.Invalid("merge", "merge is for a boss; a cut always removes material from the bodies it meets");
        return new ExtrudeSpec
        {
            Cut = r.Mode == FeatureMode.Cut,
            End = e,
            DepthMm = r.DepthMm ?? 0,
            Reverse = r.Reverse == true,
            DraftDeg = r.DraftDeg ?? 0,
            DraftOutward = r.DraftOutward == true,
            Merge = r.Merge ?? true,
            Direction2 = r.Direction2 == null ? null : Second(r.Direction2, e),
            Thin = r.Thin == null ? null : Thin(r.Thin),
        };
    }

    /// <summary>The second direction, checked as the first is, its fields reported under /direction2.</summary>
    static ExtrudeSecond Second(ExtrudeDirection d, ExtrudeEnd first)
    {
        if (first == ExtrudeEnd.MidPlane) throw FeatureFacts.Invalid("direction2", "a mid_plane extrude already goes both ways, so it takes no direction2");
        var e = d.End ?? ExtrudeEnd.Blind;
        string end = WireNames.ForEnum(e);
        if (e == ExtrudeEnd.MidPlane) throw FeatureFacts.Invalid("/direction2/end", "a second direction cannot end mid_plane");
        bool upToEnd = e == ExtrudeEnd.UpToVertex || e == ExtrudeEnd.UpToSurface;
        if (e == ExtrudeEnd.Blind) FeatureFacts.Length(d.DepthMm, "/direction2/depth_mm", "a blind second direction");
        else if (d.DepthMm != null) throw FeatureFacts.Invalid("/direction2/depth_mm", $"depth_mm is for a blind second direction, and this one ends {end}");
        if (upToEnd && d.UpTo == null) throw FeatureFacts.Invalid("/direction2/up_to", $"a second direction that ends {end} needs up_to");
        if (!upToEnd && d.UpTo != null) throw FeatureFacts.Invalid("/direction2/up_to", $"up_to is for up_to_vertex and up_to_surface only, and this second direction ends {end}");
        if (d.DraftDeg != null && !(d.DraftDeg > 0 && d.DraftDeg < 90)) throw FeatureFacts.Invalid("/direction2/draft_deg", "draft_deg must be more than 0 and less than 90 degrees");
        return new ExtrudeSecond { End = e, DepthMm = d.DepthMm ?? 0, DraftDeg = d.DraftDeg ?? 0 };
    }

    /// <summary>The thin wall, its fields reported under /thin.</summary>
    static ThinSpec Thin(ExtrudeThin t)
    {
        var side = t.Side ?? ThinSide.OneSide;
        var spec = new ThinSpec { Side = side, ThicknessMm = FeatureFacts.Length(t.ThicknessMm, "/thin/thickness_mm", "a thin wall") };
        if (side == ThinSide.TwoSides) spec.Thickness2Mm = FeatureFacts.Length(t.Thickness2Mm, "/thin/thickness2_mm", "a thin wall on two sides");
        else if (t.Thickness2Mm != null) throw FeatureFacts.Invalid("/thin/thickness2_mm", "thickness2_mm is for a two_sides thin wall only");
        return spec;
    }
}

public sealed class FeatureRevolveOperation : ModelChangeOperation<FeatureRevolveRequest, FeatureResult>
{
    public FeatureRevolveOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureRevolveRequest request) => request.Document;

    static string What(FeatureRevolveRequest request) => request.Mode == FeatureMode.Cut ? "cut revolve" : "boss revolve";

    protected override FeatureResult Change(ISwDocument doc, FeatureRevolveRequest request, ModelTransaction tx, IOperationContext context)
    {
        double angle = request.AngleDeg ?? 360;
        if (double.IsNaN(angle) || !(angle > 0 && angle <= 360)) throw FeatureFacts.Invalid("angle_deg", "angle_deg must be more than 0 and at most 360 degrees");
        bool cut = request.Mode == FeatureMode.Cut;
        if (cut && request.Merge != null) throw FeatureFacts.Invalid("merge", "merge is for a boss; a cut always removes material from the bodies it meets");
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What(request));
        var (axis, kind) = references.Resolve(doc, request.Axis, "axis", ReferenceKind.SketchEntity, ReferenceKind.Axis, ReferenceKind.Edge);
        var info = doc.Describe(axis);
        if (kind == ReferenceKind.SketchEntity && info.Feature != profile.Name)
            throw FeatureFacts.Invalid("axis", $"axis: that line is in {info.Feature}, not {profile.Name}; give a line or centerline of the sketch being revolved, a reference axis or a straight edge");
        if (kind != ReferenceKind.Axis && info.Geometry != GeometryType.Line)
            throw FeatureFacts.Invalid("axis", $"axis: a revolve turns about a straight line, and this is a {WireNames.ForEnum(info.Geometry)}");
        var spec = new RevolveSpec { Cut = cut, Axis = axis, AngleDeg = angle, Reverse = request.Reverse == true, Merge = request.Merge ?? true };
        var feature = doc.Revolve(sketch, spec)
            ?? throw FeatureFacts.NotMade(doc, What(request), $"from {profile.Name}", "check that its profile is closed and lies wholly on one side of the axis");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    /// <summary>The feature is a revolution, the volume moved the right way, and a boss on an empty part about a line of its own sketch is exactly Pappus' volume.</summary>
    protected override void Check(ISwDocument doc, FeatureRevolveRequest request, FeatureResult result)
    {
        bool cut = request.Mode == FeatureMode.Cut;
        FeatureFacts.ExpectType(doc, result, cut ? "RevCut" : "Revolution", What(request));
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureFacts.VolumeChanged(result, What(request), VolumeBefore, after, cut ? -1 : 1);
        if (!cut && BodiesBefore == 0)
        {
            var (sketch, _) = Services.References.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
            var (axis, kind) = Services.References.Resolve(doc, request.Axis, "axis", ReferenceKind.SketchEntity, ReferenceKind.Axis, ReferenceKind.Edge);
            var contents = doc.ReadSketch(sketch);
            var region = FeatureFacts.Region(contents);
            string? axisName = kind == ReferenceKind.SketchEntity ? doc.Describe(axis).Name : null;
            var line = axisName == null ? null : contents.Segments.FirstOrDefault(s => s.Name == axisName && s.Start != null && s.End != null);
            if (region != null && line != null)
            {
                double angle = request.AngleDeg ?? 360;
                double expected = FeatureChecks.PappusVolume(region, line.Start!, line.End!, angle);
                string why = FormattableString.Invariant($"{angle:0.###}° × {region.AreaMm2:0.###} mm² about {line.Name}");
                FeatureChecks.VolumeChangeIs(What(request), expected, after - VolumeBefore, why);
                result.Checks.Add(FormattableString.Invariant($"the volume grew by {after - VolumeBefore:0.###} mm³, Pappus' {why}"));
            }
        }
        FeatureFacts.MadeFaces(result, What(request));
    }
}

public sealed class FeatureFilletOperation : ModelChangeOperation<FeatureFilletRequest, FeatureResult>
{
    public FeatureFilletOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureFilletRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, FeatureFilletRequest request, ModelTransaction tx, IOperationContext context)
    {
        double radius = FeatureFacts.Length(request.RadiusMm, "radius_mm", "a fillet");
        if ((request.Edges?.Count ?? 0) + (request.Faces?.Count ?? 0) == 0) throw FeatureFacts.Invalid("edges", "a fillet needs edges, faces or both to round");
        var edges = FeatureFacts.Entities(Services.References, doc, request.Edges, "edges", ReferenceKind.Edge);
        var faces = FeatureFacts.Entities(Services.References, doc, request.Faces, "faces", ReferenceKind.Face);
        var feature = doc.Fillet(edges.Concat(faces).ToList(), radius)
            ?? throw FeatureFacts.NotMade(doc, "fillet", FormattableString.Invariant($"of {radius:0.###} mm"), "check that the radius fits the edges and faces it rounds");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The feature is a fillet, the edges named are gone and faces replaced them, and the volume changed.</summary>
    protected override void Check(ISwDocument doc, FeatureFilletRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Fillet", "fillet");
        FeatureFacts.EdgesGone(Services.References, doc, request.Edges, result, "fillet");
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureChecks.VolumeMoved("fillet", VolumeBefore, after);
        result.Checks.Add(FormattableString.Invariant($"the volume changed: {VolumeBefore:0.###} -> {after:0.###} mm³"));
        FeatureFacts.MadeFaces(result, "fillet");
    }
}

public sealed class FeatureChamferOperation : ModelChangeOperation<FeatureChamferRequest, FeatureResult>
{
    public FeatureChamferOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureChamferRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, FeatureChamferRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = new ChamferSpec { DistanceMm = FeatureFacts.Length(request.DistanceMm, "distance_mm", "a chamfer") };
        if (request.AngleDeg != null && request.Distance2Mm != null)
            throw FeatureFacts.Invalid("distance2_mm", "a chamfer has an angle or a second distance, not both");
        if (request.Distance2Mm != null) spec.Distance2Mm = FeatureFacts.Length(request.Distance2Mm, "distance2_mm", "a distance-distance chamfer");
        if (request.AngleDeg != null)
        {
            if (!(request.AngleDeg > 0 && request.AngleDeg < 90)) throw FeatureFacts.Invalid("angle_deg", "angle_deg must be more than 0 and less than 90 degrees");
            spec.AngleDeg = request.AngleDeg.Value;
        }
        if (request.Edges.Count == 0) throw FeatureFacts.Invalid("edges", "a chamfer needs the edges to bevel");
        var edges = FeatureFacts.Entities(Services.References, doc, request.Edges, "edges", ReferenceKind.Edge);
        var feature = doc.Chamfer(edges, spec)
            ?? throw FeatureFacts.NotMade(doc, "chamfer", FormattableString.Invariant($"of {spec.DistanceMm:0.###} mm"), "check that the distance fits the faces beside each edge");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The feature is a chamfer, the edges named are gone and faces replaced them, and the volume changed.</summary>
    protected override void Check(ISwDocument doc, FeatureChamferRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Chamfer", "chamfer");
        FeatureFacts.EdgesGone(Services.References, doc, request.Edges, result, "chamfer");
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureChecks.VolumeMoved("chamfer", VolumeBefore, after);
        result.Checks.Add(FormattableString.Invariant($"the volume changed: {VolumeBefore:0.###} -> {after:0.###} mm³"));
    }
}

public sealed class FeatureSuppressOperation : ModelChangeOperation<FeatureSuppressRequest, FeatureSuppressResult>
{
    public FeatureSuppressOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureSuppressRequest request) => request.Document;

    protected override FeatureSuppressResult Change(ISwDocument doc, FeatureSuppressRequest request, ModelTransaction tx, IOperationContext context)
    {
        bool want = request.Suppressed ?? true;
        var features = FeatureFacts.Features(Services.References, doc, request.Features);
        var result = new FeatureSuppressResult();
        foreach (var (feature, name) in features)
        {
            bool now = FeatureFacts.IsSuppressed(doc, name);
            if (now == want)
            {
                result.Unchanged.Add(name);
                continue;
            }
            tx.Edited(new ChangedItem { Name = name, Property = "suppressed", Before = Text(now), After = Text(want) }, () =>
            {
                doc.Suppress(feature, now);
                doc.Rebuild(false);
            });
            if (!doc.Suppress(feature, want))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not {(want ? "suppress" : "unsuppress")} {name}");
        }
        if (!doc.Rebuild(false)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS could not rebuild {doc.Info().Title} after {(want ? "suppressing" : "unsuppressing")} {string.Join(", ", features.Select(f => f.Name))}");
        result.Features = features.Select(f => Services.References.Describe(doc, f.Feature)).ToList();
        return result;
    }

    /// <summary>Each feature named reads as asked in the tree.</summary>
    protected override void Check(ISwDocument doc, FeatureSuppressRequest request, FeatureSuppressResult result)
    {
        bool want = request.Suppressed ?? true;
        foreach (var f in result.Features)
        {
            string name = f.Name ?? "";
            if (FeatureFacts.IsSuppressed(doc, name) != want)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{name} is {(want ? "not suppressed" : "still suppressed")} after the rebuild",
                    new JsonObject { ["check"] = "suppressed", ["feature"] = name, ["expected"] = want });
            }
        }
    }

    static string Text(bool suppressed) => suppressed ? "suppressed" : "unsuppressed";
}

public sealed class FeatureDeleteOperation : ModelChangeOperation<FeatureDeleteRequest, FeatureDeleteResult>
{
    public FeatureDeleteOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureDeleteRequest request) => request.Document;

    protected override FeatureDeleteResult Change(ISwDocument doc, FeatureDeleteRequest request, ModelTransaction tx, IOperationContext context)
    {
        bool withChildren = request.Children == DeleteChildren.Delete;
        var features = FeatureFacts.Features(Services.References, doc, request.Features);
        var named = new HashSet<string>(features.Select(f => f.Name), StringComparer.Ordinal);
        var children = features.ToDictionary(f => f.Name, f => Descendants(doc, f.Feature).Where(c => !named.Contains(c)).ToList());
        var refused = children.Where(c => c.Value.Count > 0).ToList();
        if (!withChildren && refused.Count > 0)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{string.Join("; ", refused.Select(r => $"{r.Key} has features built on it ({string.Join(", ", r.Value)})"))}: give children: delete to delete them too, or delete them first. Nothing was deleted",
                new JsonObject { ["field"] = "children", ["children"] = refused.SelectMany(r => r.Value).Distinct().Select(n => (object?)n).ToList() });
        }
        var result = new FeatureDeleteResult();
        foreach (var (feature, name) in features)
        {
            if (doc.FeatureByName(name) == null)
            {
                // Built on a feature named earlier, and deleted with it.
                if (!result.Deleted.Contains(name)) result.Deleted.Add(name);
                continue;
            }
            var gone = new List<string> { name };
            if (withChildren) gone.AddRange(children[name]);
            doc.Delete(feature, withChildren, absorbed: request.KeepAbsorbed != true);
            foreach (var g in gone.Where(g => !result.Deleted.Contains(g)))
            {
                result.Deleted.Add(g);
                tx.Edited(new ChangedItem { Name = g, Property = "deleted", Before = "present", After = "deleted" },
                    () => throw new SwAgentException(ErrorCode.OperationFailed, $"{g} was deleted and cannot be made again from its reference"));
            }
        }
        if (!doc.Rebuild(false)) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS could not rebuild {doc.Info().Title} after deleting {string.Join(", ", result.Deleted)}");
        return result;
    }

    /// <summary>
    /// Every feature built on <paramref name="feature"/>, at any depth (children, their children …), by name, each once:
    /// swDelete_Children deletes them all, so all are refused, reported and checked.
    /// </summary>
    static List<string> Descendants(ISwDocument doc, object feature)
    {
        var seen = new List<string>();
        var queue = new Queue<object>();
        queue.Enqueue(feature);
        while (queue.Count > 0)
        {
            foreach (var name in doc.ChildrenOf(queue.Dequeue()))
            {
                if (seen.Contains(name)) continue;
                seen.Add(name);
                if (doc.FeatureByName(name) is object child) queue.Enqueue(child);
            }
        }
        return seen;
    }

    /// <summary>Every feature deleted is gone from the tree.</summary>
    protected override void Check(ISwDocument doc, FeatureDeleteRequest request, FeatureDeleteResult result)
    {
        var left = result.Deleted.Where(n => doc.FeatureByName(n) != null).ToList();
        if (left.Count > 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{string.Join(", ", left)} is still in the part after it was deleted",
                new JsonObject { ["check"] = "deleted", ["left"] = left.Select(n => (object?)n).ToList() });
        }
    }
}

/// <summary>What every feature operation shares: request checks, the feature recorded in the transaction, the result filled, and the common checks.</summary>
public static class FeatureFacts
{
    public static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });

    /// <summary>A length that must be given, more than 0 and at most <see cref="SketchDraft.Reach"/> mm.</summary>
    public static double Length(double? value, string field, string what)
    {
        if (value == null) throw Invalid(field, $"{what} needs {field}");
        double v = value.Value;
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0 || v > SketchDraft.Reach)
            throw Invalid(field, $"{field} must be more than 0 and at most {SketchDraft.Reach} mm, not {v}");
        return v;
    }

    /// <summary>Every reference in <paramref name="texts"/> resolved to its entity, of <paramref name="kind"/>, each once; fields /<paramref name="field"/>/i.</summary>
    public static List<object> Entities(ReferenceService references, ISwDocument doc, IReadOnlyList<string>? texts, string field, ReferenceKind kind)
    {
        var list = new List<object>();
        if (texts == null) return list;
        for (int i = 0; i < texts.Count; i++)
        {
            string at = $"/{field}/{i}";
            int seen = texts.Take(i).ToList().IndexOf(texts[i]);
            if (seen >= 0) throw Invalid(at, $"{at} names the same {WireNames.ForEnum(kind)} as /{field}/{seen}");
            list.Add(references.Resolve(doc, texts[i], at, kind).Entity);
        }
        return list;
    }

    /// <summary>Every edge named no longer resolves (the feature consumed it), and the feature made at least one face for each.</summary>
    public static void EdgesGone(ReferenceService references, ISwDocument doc, IReadOnlyList<string>? edges, FeatureResult result, string what)
    {
        if (edges == null) return;
        for (int i = 0; i < edges.Count; i++)
        {
            try
            {
                references.Resolve(doc, edges[i], $"/edges/{i}", ReferenceKind.Edge);
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved)
            {
                continue;
            }
            throw new SwAgentException(ErrorCode.VerificationFailed, $"/edges/{i} is still an edge of the part after the {what}: SOLIDWORKS did not {what} it",
                new JsonObject { ["check"] = "edges_gone", ["field"] = $"/edges/{i}" });
        }
        if (result.Faces.Count < edges.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} made {result.Faces.Count} faces for {edges.Count} edges",
                new JsonObject { ["check"] = "faces", ["expected"] = edges.Count, ["measured"] = result.Faces.Count });
        }
        result.Checks.Add($"the {edges.Count} edges named are gone, and the {what} made {result.Faces.Count} faces");
    }

    /// <summary>The features named (any kind of feature: features, sketches, planes, axes, points), each once, with their names; fields /features/i.</summary>
    public static List<(object Feature, string Name)> Features(ReferenceService references, ISwDocument doc, IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) throw Invalid("features", "name at least one feature");
        var list = new List<(object, string)>();
        for (int i = 0; i < texts.Count; i++)
        {
            string at = $"/features/{i}";
            var (entity, _) = references.Resolve(doc, texts[i], at, ReferenceKind.Feature, ReferenceKind.Sketch, ReferenceKind.Plane, ReferenceKind.Axis, ReferenceKind.Point);
            string name = doc.Describe(entity).Name ?? "?";
            if (list.Any(f => f.Item2 == name)) throw Invalid(at, $"{at} names {name} again");
            list.Add((entity, name));
        }
        return list;
    }

    /// <summary>Whether the tree reads the feature named as suppressed (false when it is not in the tree).</summary>
    public static bool IsSuppressed(ISwDocument doc, string name) => doc.Features().FirstOrDefault(f => f.Name == name)?.Suppressed ?? false;

    /// <summary>The sketch's contents, which must hold something that is not construction geometry.</summary>
    public static SketchContents Profile(ISwDocument doc, object sketch, string field, string what)
    {
        var contents = doc.ReadSketch(sketch);
        if (!contents.Segments.Any(s => !s.Construction))
            throw Invalid(field, $"{field}: {contents.Name} has nothing a {what} can use; draw a closed profile in it (sketch.create)");
        return contents;
    }

    /// <summary>The region the sketch's lines and circles enclose, or null when the profile has arcs, splines or open chains.</summary>
    public static ProfileRegion? Region(SketchContents c)
    {
        var drawn = c.Segments.Where(s => !s.Construction).ToList();
        if (drawn.Any(s => s.Kind != SketchSegmentKind.Line && s.Kind != SketchSegmentKind.Circle)) return null;
        return Profiles.Region(
            drawn.Where(s => s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null).Select(s => (s.Start!, s.End!)).ToList(),
            drawn.Where(s => s.Kind == SketchSegmentKind.Circle && s.Center != null && s.RadiusMm != null).Select(s => (s.Center!, s.RadiusMm!.Value)).ToList());
    }

    /// <summary>operation_failed for a feature SOLIDWORKS did not make, with its rebuild errors when it gave any.</summary>
    public static SwAgentException NotMade(ISwDocument doc, string what, string from, string hint)
    {
        var errors = doc.WhatsWrong().Where(i => !i.Warning).Select(i => $"{i.Feature}: {i.Code}").ToList();
        return new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS made no {what} {from}{(errors.Count > 0 ? $" ({string.Join(", ", errors)})" : "")}; {hint}. Nothing was changed",
            new JsonObject { ["errors"] = errors.Select(e => (object?)e).ToList() });
    }

    /// <summary>Records a feature just made in the transaction (deleting it, never its sketch, takes it back), and fills the result.</summary>
    public static FeatureResult Made(ReferenceService references, ISwDocument doc, object feature, ModelTransaction tx)
    {
        var made = new CreatedItem { Name = doc.Describe(feature).Name ?? "a feature", Kind = doc.KindOf(feature) ?? ReferenceKind.Feature };
        tx.Made(made, () =>
        {
            doc.Delete(feature, children: false, absorbed: false);
            doc.Rebuild(false);
        });
        var result = new FeatureResult
        {
            Feature = references.Describe(doc, feature),
            Faces = doc.FacesOf(feature).Select(f => references.Describe(doc, f)).ToList(),
        };
        made.Reference = result.Feature.Reference;
        return result;
    }

    /// <summary>The feature is in the tree with SOLIDWORKS' type name <paramref name="type"/> (IFeature.GetTypeName2).</summary>
    public static void ExpectType(ISwDocument doc, FeatureResult result, string type, string what) => ExpectType(doc, result, new[] { type }, what);

    /// <summary>The feature is in the tree with one of SOLIDWORKS' type names <paramref name="types"/> (IFeature.GetTypeName2).</summary>
    public static void ExpectType(ISwDocument doc, FeatureResult result, IReadOnlyList<string> types, string what)
    {
        string name = result.Feature.Name ?? "";
        var record = doc.Features().FirstOrDefault(f => f.Name == name)
            ?? throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} {name} is not in the feature tree after it was made", new JsonObject { ["check"] = "feature_exists" });
        if (!types.Contains(record.Type))
        {
            string expected = string.Join(" or ", types);
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{name} is a {record.Type}, not the {expected} a {what} makes",
                new JsonObject { ["check"] = "feature_type", ["expected"] = expected, ["measured"] = record.Type });
        }
    }

    /// <summary>The volume moved the way the feature implies (<see cref="FeatureChecks.VolumeChanged"/>), recorded in the result's checks.</summary>
    public static void VolumeChanged(FeatureResult result, string what, double before, double after, int sign)
    {
        FeatureChecks.VolumeChanged(what, before, after, sign);
        string way = sign > 0 ? "grew" : sign < 0 ? "shrank" : "stayed";
        result.Checks.Add(FormattableString.Invariant($"the volume {way}: {before:0.###} -> {after:0.###} mm³"));
    }

    /// <summary>The part has a solid body after the feature (design 371: a sweep or loft leaves "a body").</summary>
    public static void HasBody(int bodies, string what)
    {
        if (bodies > 0) return;
        throw new SwAgentException(ErrorCode.VerificationFailed, $"the part has no solid body after the {what}",
            new JsonObject { ["check"] = "body", ["expected"] = "at least 1", ["measured"] = bodies });
    }

    /// <summary>The model-space vector from start to end of the sketch's one drawn segment, when that is a line; null otherwise.</summary>
    public static (Vector3 Direction, double LengthMm)? OnlyLine(SketchContents c)
    {
        var drawn = c.Segments.Where(s => !s.Construction).ToList();
        if (drawn.Count != 1 || drawn[0].Kind != SketchSegmentKind.Line || drawn[0].Start == null || drawn[0].End == null) return null;
        double dx = drawn[0].End!.X - drawn[0].Start!.X, dy = drawn[0].End!.Y - drawn[0].Start!.Y;
        var d = new Vector3(dx * c.XAxis.X + dy * c.YAxis.X, dx * c.XAxis.Y + dy * c.YAxis.Y, dx * c.XAxis.Z + dy * c.YAxis.Z);
        return (d, Math.Sqrt(dx * dx + dy * dy));
    }

    public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Every entity named in <paramref name="field"/> no longer resolves: the feature consumed it (a shell's open faces).</summary>
    /// <remarks>
    /// When one still resolves, the failure says what SOLIDWORKS resolves it to now (3.D live run, round 1: a shell's
    /// open face still resolved, and whether to the whole face or to what is left of it could not be read), with
    /// <paramref name="also"/> (the volume, for a shell).
    /// </remarks>
    public static void Gone(ReferenceService references, ISwDocument doc, IReadOnlyList<string>? texts, string field, ReferenceKind kind, FeatureResult result, string what, string? also = null)
    {
        if (texts == null || texts.Count == 0) return;
        for (int i = 0; i < texts.Count; i++)
        {
            object entity;
            try
            {
                (entity, _) = references.Resolve(doc, texts[i], $"/{field}/{i}", kind);
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved)
            {
                continue;
            }
            var now = doc.Describe(entity);
            string described = EntityFilter.Describe(now) + (now.Normal != null ? $", normal {now.Normal}" : "") + (now.Origin != null ? $", at {now.Origin}" : "");
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"/{field}/{i} is still a {WireNames.ForEnum(kind)} of the part after the {what}: it resolves to {described}{(also == null ? "" : "; " + also)}",
                new JsonObject { ["check"] = "gone", ["field"] = $"/{field}/{i}", ["measured"] = described });
        }
        result.Checks.Add($"the {texts.Count} {WireNames.ForEnum(kind)}s named in {field} are gone");
    }

    /// <summary>The feature made at least one face.</summary>
    public static void MadeFaces(FeatureResult result, string what)
    {
        if (result.Faces.Count == 0)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the {what} {result.Feature.Name} made no faces", new JsonObject { ["check"] = "faces" });
    }
}
