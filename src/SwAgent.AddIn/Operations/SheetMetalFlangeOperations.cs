using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the flange and hem operations share: the request's allowance, the new features' bends, and the result.</summary>
static class Flanges
{
    /// <summary>An angle more than 0 and less than <paramref name="below"/> degrees.</summary>
    public static double Angle(double? value, double fallback, string field, double below)
    {
        double a = value ?? fallback;
        if (double.IsNaN(a) || a <= 0 || a >= below)
            throw FeatureFacts.Invalid(field, $"{field} must be more than 0 and less than {below.ToString(CultureInfo.InvariantCulture)} degrees, not {a.ToString(CultureInfo.InvariantCulture)}");
        return a;
    }

    /// <summary>A distance that may be 0: 0 or more and at most <see cref="SketchDraft.Reach"/> mm; <paramref name="fallback"/> when absent.</summary>
    public static double Distance(double? value, double fallback, string field)
    {
        double d = value ?? fallback;
        if (double.IsNaN(d) || double.IsInfinity(d) || d < 0 || d > SketchDraft.Reach)
            throw FeatureFacts.Invalid(field, $"{field} must be 0 or more and at most {SketchDraft.Reach} mm, not {d.ToString(CultureInfo.InvariantCulture)}");
        return d;
    }

    /// <summary>The bends the features named made, in tree order.</summary>
    public static List<BendRecord> BendsOf(ISwDocument doc, IEnumerable<string> features)
    {
        var names = new HashSet<string>(features, StringComparer.Ordinal);
        return doc.SheetMetal.Bends().Where(b => names.Contains(b.Owner)).ToList();
    }

    /// <summary>Each bend reads back the angle, inner radius and allowance asked.</summary>
    public static void BendsAre(IEnumerable<BendRecord> bends, double? angleDeg, double radiusMm, BendAllowance allowance)
    {
        foreach (var b in bends)
        {
            if (angleDeg != null) FeatureChecks.Near("bend_angle", $"{b.Name}'s angle", angleDeg.Value, b.AngleDeg, 1e-3, "°");
            FeatureChecks.Near("bend_radius", $"{b.Name}'s inner radius", radiusMm, b.RadiusMm, 1e-6, "mm");
            if (!SheetMetalParts.Same(allowance, b.Allowance))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"{b.Name}'s allowance reads back as {b.Allowance}, not the {allowance} asked",
                    new JsonObject { ["check"] = "allowance", ["bend"] = b.Name, ["expected"] = allowance.ToString(), ["measured"] = b.Allowance.ToString() });
            }
        }
    }

    /// <summary>The result for the features made: the first as feature, all of them as features when there is more than one, and every face.</summary>
    public static SheetMetalFeatureResult Result(IReadOnlyList<FeatureResult> made) => new SheetMetalFeatureResult
    {
        Feature = made[0].Feature,
        Features = made.Count > 1 ? made.Select(m => m.Feature).ToList() : null,
        Faces = made.SelectMany(m => m.Faces).ToList(),
    };

    /// <summary>How far the part reaches along an axis-aligned direction (its box's far side that way), or null when the direction is not along x, y or z.</summary>
    public static double? Reach(Box3 box, Vector3 n)
    {
        if (Math.Abs(n.X) > 1 - 1e-9) return n.X > 0 ? box.Max.X : -box.Min.X;
        if (Math.Abs(n.Y) > 1 - 1e-9) return n.Y > 0 ? box.Max.Y : -box.Min.Y;
        if (Math.Abs(n.Z) > 1 - 1e-9) return n.Z > 0 ? box.Max.Z : -box.Min.Z;
        return null;
    }
}

public sealed class SheetMetalEdgeFlangeOperation : ModelChangeOperation<SheetMetalEdgeFlangeRequest, SheetMetalFeatureResult>
{
    const string What = "edge flange";

    /// <summary>A flange made: its feature, the edge it was made on as read just before, and its width.</summary>
    sealed class Made
    {
        public Made(FeatureResult feature, SheetEdge edge, double widthMm)
        {
            Feature = feature;
            Edge = edge;
            WidthMm = widthMm;
        }

        public FeatureResult Feature { get; }
        public SheetEdge Edge { get; }
        public double WidthMm { get; }
    }

    readonly List<Made> _made = new List<Made>();
    EdgeFlangeSection? _section;
    SheetDefaults? _defaults;
    BendAllowance? _allowance;
    double _offset;
    Box3? _boxBefore;

    public SheetMetalEdgeFlangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalEdgeFlangeRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalEdgeFlangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        _made.Clear();
        _section = null;
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        if (request.Edges.Count == 0) throw FeatureFacts.Invalid("edges", "name at least one edge for an edge flange (sheetmetal.read gives the base face, model.find its edges)");
        double angle = Flanges.Angle(request.AngleDeg, 90, "angle_deg", 180);
        double length = FeatureFacts.Length(request.LengthMm, "length_mm", "an edge flange");
        double? radius = request.BendRadiusMm == null ? null : FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a bend");
        var allowance = request.Allowance == null ? null : SheetMetalParts.Allowance(request.Allowance, "allowance");
        double offset = Flanges.Distance(request.OffsetMm, 0, "offset_mm");
        var from = request.LengthFrom ?? FlangeLengthFrom.OuterVirtualSharp;
        var position = request.Position ?? BendPosition.MaterialInside;
        double r = radius ?? defaults.BendRadiusMm, t = defaults.ThicknessMm;
        var section = FlangeGeometry.EdgeFlange(angle, r, t, length, from, position)
            ?? throw FeatureFacts.Invalid("length_mm", FormattableString.Invariant($"length_mm {length} mm from the {WireNames.ForEnum(from).Replace('_', ' ')} leaves no straight flange after a {angle}° bend of inner radius {r} mm in {t} mm sheet; make it longer"));
        var references = Services.References;
        var edges = FeatureFacts.Entities(references, doc, request.Edges, "edges", ReferenceKind.Edge);
        for (int i = 0; i < edges.Count; i++)
        {
            var edge = SheetMetalParts.Edge(doc, edges[i], $"/edges/{i}", t);
            if (2 * offset >= edge.LengthMm)
                throw FeatureFacts.Invalid("offset_mm", FormattableString.Invariant($"offset_mm {offset} mm at both ends leaves nothing of /edges/{i}, which is {edge.LengthMm:0.###} mm long"));
        }
        _defaults = defaults;
        _allowance = allowance;
        _offset = offset;
        _section = section;
        _boxBefore = doc.BoundingBox(true);
        var spec = new EdgeFlangeSpec { AngleDeg = angle, RadiusMm = radius, Position = position, LengthFrom = from, Allowance = allowance };
        for (int i = 0; i < request.Edges.Count; i++)
        {
            // Read again just before its flange: an earlier flange in this call may have moved or shortened it.
            string at = $"/edges/{i}";
            var (entity, _) = references.Resolve(doc, request.Edges[i], at, ReferenceKind.Edge);
            var edge = SheetMetalParts.Edge(doc, entity, at, t);
            var sketch = doc.SheetMetal.EdgeFlangeSketch(entity, angle)
                ?? throw FeatureFacts.NotMade(doc, "sketch for an edge flange", $"on {at}", "check that it is a straight edge of the sheet with no flange on it yet");
            string sketchName = doc.Describe(sketch).Name ?? "the edge flange's sketch";
            tx.Made(new CreatedItem { Name = sketchName, Kind = ReferenceKind.Sketch, Reference = references.Describe(doc, sketch).Reference }, () =>
            {
                if (doc.FeatureByName(sketchName) is object left)
                {
                    doc.Delete(left, children: false, absorbed: false);
                    doc.Rebuild(false);
                }
            });
            Draw(doc, sketch, edge, angle, section.HeightMm, offset);
            var flange = doc.SheetMetal.EdgeFlange(entity, sketch, spec)
                ?? throw FeatureFacts.NotMade(doc, What, $"on {at}", "check that the edge has no flange on it yet and that the flange clears the rest of the part");
            _made.Add(new Made(FeatureFacts.Made(references, doc, flange, tx), edge, edge.LengthMm - 2 * offset));
        }
        return Flanges.Result(_made.Select(m => m.Feature).ToList());
    }

    /// <summary>The flange's profile: the edge, pulled in at both ends, and the same line at the flange's end, drawn as four lines in the sketch SOLIDWORKS made.</summary>
    static void Draw(ISwDocument doc, object sketch, SheetEdge edge, double angle, double height, double offset)
    {
        var corners = FlangeGeometry.Profile(edge.Start, edge.End, edge.Normal, edge.Outward, angle, height, offset);
        var editor = doc.EditSketch(sketch);
        try
        {
            var at = corners.Select(editor.ToSketch).ToList();
            for (int i = 0; i < 4; i++) editor.Line(at[i], at[(i + 1) % 4], false);
        }
        finally
        {
            editor.Close();
        }
    }

    /// <summary>Each flange made one bend at the angle, radius and allowance asked; the volume grew by what the flange geometry gives; the part reaches the flanges' height.</summary>
    protected override void Check(ISwDocument doc, SheetMetalEdgeFlangeRequest request, SheetMetalFeatureResult result)
    {
        var section = _section!;
        var defaults = _defaults!;
        var checks = result.Checks;
        var allowance = _allowance ?? defaults.Allowance;
        foreach (var made in _made)
        {
            var probe = new FeatureResult { Feature = made.Feature.Feature, Faces = made.Feature.Faces, Checks = checks };
            FeatureFacts.ExpectType(doc, probe, "EdgeFlange", What);
            FeatureFacts.MadeFaces(probe, What);
        }
        var bends = Flanges.BendsOf(doc, _made.Select(m => m.Feature.Feature.Name!));
        foreach (var made in _made)
        {
            int count = bends.Count(b => b.Owner == made.Feature.Feature.Name);
            if (count != 1)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed, $"the edge flange {made.Feature.Feature.Name} made {count} bends, not one",
                    new JsonObject { ["check"] = "bends", ["feature"] = made.Feature.Feature.Name, ["expected"] = 1, ["measured"] = count });
            }
        }
        Flanges.BendsAre(bends, section.AngleDeg, section.RadiusMm, allowance);
        checks.Add(FormattableString.Invariant($"{bends.Count} bends, one per flange, at {section.AngleDeg:0.###}°, inner radius {section.RadiusMm:0.####} mm and {allowance}"));
        double added = Volume(doc, doc.SolidBodyCount()) - VolumeBefore;
        double width = _made.Sum(m => m.WidthMm);
        double expected = width * section.AddedPerWidthMm2;
        string why = FormattableString.Invariant($"{width:0.###} mm of flange × {section.AddedPerWidthMm2:0.####} mm²: a {section.StraightMm:0.####} mm straight part and a {section.AngleDeg:0.###}° bend of R {section.RadiusMm:0.####}, less {section.TrimMm:0.####} mm of sheet taken back, {section.ThicknessMm:0.####} mm thick");
        if (_offset == 0)
        {
            FeatureChecks.VolumeChangeIs(What, expected, added, why);
            checks.Add(FormattableString.Invariant($"the volume grew by {added:0.###} mm³, the flange geometry's {why}"));
        }
        else
        {
            // SOLIDWORKS cuts a relief into the sheet beside each end of a flange shorter than its edge: at most the relief
            // ratio's width of the thickness, as deep as the sheet taken back plus that width, through the thickness.
            double reliefWidth = defaults.ReliefRatio * section.ThicknessMm;
            double reliefs = _made.Count * 2 * reliefWidth * (section.TrimMm + reliefWidth) * section.ThicknessMm;
            if (added > expected * (1 + FeatureChecks.VolumeTolerance) + 1e-9 || added < expected - reliefs - 1e-9)
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    FormattableString.Invariant($"the edge flange changed the volume by {added:0.###} mm³, outside the {expected - reliefs:0.###} to {expected:0.###} mm³ its profile and reliefs give ({why})"),
                    new JsonObject { ["check"] = "volume", ["expected"] = expected, ["reliefs"] = reliefs, ["measured"] = added, ["why"] = why });
            }
            checks.Add(FormattableString.Invariant($"the volume grew by {added:0.###} mm³, the flange geometry's {expected:0.###} mm³ ({why}) less at most {reliefs:0.###} mm³ of reliefs at the flanges' ends"));
        }
        Height(doc, section, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }

    /// <summary>Up to 90°, the part now reaches each flange's end: its rise above the face the edge bounds, along that face's normal.</summary>
    void Height(ISwDocument doc, EdgeFlangeSection section, List<string> checks)
    {
        var boxAfter = doc.BoundingBox(true);
        if (section.RiseMm == null || _boxBefore == null || boxAfter == null)
        {
            checks.Add(section.RiseMm == null ? "the part's height was not compared: past 90° the bend may stand above the flange's end" : "the part's height was not compared: SOLIDWORKS gave no box");
            return;
        }
        foreach (var made in _made)
        {
            var n = made.Edge.Normal;
            double? before = Flanges.Reach(_boxBefore, n), after = Flanges.Reach(boxAfter, n);
            if (before == null || after == null || made.Edge.FaceOrigin == null)
            {
                checks.Add($"{made.Feature.Feature.Name}'s height was not compared: its face's normal is not along x, y or z");
                continue;
            }
            double level = FeatureFacts.Dot(n, new Vector3(made.Edge.FaceOrigin.X, made.Edge.FaceOrigin.Y, made.Edge.FaceOrigin.Z)) + section.RiseMm.Value;
            double expected = Math.Max(before.Value, level);
            FeatureChecks.Near("height", $"how far the part reaches along {made.Feature.Feature.Name}'s face normal", expected, after.Value, 1e-3, "mm");
            checks.Add(FormattableString.Invariant($"{made.Feature.Feature.Name} rises {section.RiseMm:0.####} mm above its face: the part reaches {after:0.####} mm along that face's normal"));
        }
    }
}

public sealed class SheetMetalMiterFlangeOperation : ModelChangeOperation<SheetMetalMiterFlangeRequest, SheetMetalFeatureResult>
{
    const string What = "miter flange";

    SheetDefaults? _defaults;
    double _radius;
    BendAllowance? _allowance;
    int _bendsPerEdge;
    double _edgesMm;
    double _profileMm;
    SheetEdge? _first;
    (Vector3 Direction, double LengthMm)? _line;
    Box3? _boxBefore;

    public SheetMetalMiterFlangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalMiterFlangeRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalMiterFlangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        if (request.Edges.Count == 0) throw FeatureFacts.Invalid("edges", "name at least one edge for a miter flange (sheetmetal.read gives the base face, model.find its edges)");
        double? radius = request.BendRadiusMm == null ? null : FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a bend");
        var allowance = request.Allowance == null ? null : SheetMetalParts.Allowance(request.Allowance, "allowance");
        double gap = Flanges.Distance(request.GapMm, 0.5, "gap_mm");
        double t = defaults.ThicknessMm;
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What);
        var edges = FeatureFacts.Entities(references, doc, request.Edges, "edges", ReferenceKind.Edge);
        var sheetEdges = edges.Select((e, i) => SheetMetalParts.Edge(doc, e, $"/edges/{i}", t)).ToList();
        var lines = profile.Segments.Where(s => !s.Construction).ToList();
        var chain = lines.All(s => s.Kind == SketchSegmentKind.Line && s.Start != null && s.End != null)
            ? BendArithmetic.Chain(lines.Select(s => (s.Start!, s.End!)).ToList())
            : null;
        if (chain == null) throw FeatureFacts.Invalid("sketch", $"{profile.Name} is not one open chain of lines: a miter flange's profile is lines end to end, each a straight part of the flange");
        var first = sheetEdges[0];
        var along = new Vector3(first.End.X - first.Start.X, first.End.Y - first.Start.Y, first.End.Z - first.Start.Z);
        double square = Math.Abs(FeatureFacts.Dot(profile.Normal, along)) / first.LengthMm;
        if (square < 1 - 1e-6)
            throw FeatureFacts.Invalid("sketch", $"{profile.Name} is not on a plane square to /edges/0: a miter flange's profile is drawn on a plane normal to its first edge (refgeom.plane with normal_to_curve at the edge's end)");
        Point3 Model(Point2 p) => new Point3(profile.Origin.X + p.X * profile.XAxis.X + p.Y * profile.YAxis.X, profile.Origin.Y + p.X * profile.XAxis.Y + p.Y * profile.YAxis.Y, profile.Origin.Z + p.X * profile.XAxis.Z + p.Y * profile.YAxis.Z);
        double miss = FlangeGeometry.NearestEndMm(new[] { Model(chain[0]), Model(chain[chain.Count - 1]) }, first.Start, first.End);
        if (miss > 1e-3)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                FormattableString.Invariant($"{profile.Name}'s nearest end is {miss:0.###} mm from an end of /edges/0; a miter flange's profile must touch an end of its first edge (SOLIDWORKS refuses one that misses, without saying why). Draw it from the edge's end. Nothing was changed"),
                new JsonObject { ["field"] = "sketch", ["gap_mm"] = miss });
        }
        _defaults = defaults;
        _radius = radius ?? defaults.BendRadiusMm;
        _allowance = allowance;
        _bendsPerEdge = BendArithmetic.OpenProfile(chain, _radius, t, 1).Bends + 1;
        _edgesMm = sheetEdges.Sum(e => e.LengthMm);
        _profileMm = Enumerable.Range(0, chain.Count - 1).Sum(i => Math.Sqrt(Math.Pow(chain[i + 1].X - chain[i].X, 2) + Math.Pow(chain[i + 1].Y - chain[i].Y, 2)));
        _first = first;
        _line = FeatureFacts.OnlyLine(profile);
        _boxBefore = doc.BoundingBox(true);
        var spec = new MiterFlangeSpec { RadiusMm = radius, GapMm = gap, Position = request.Position ?? BendPosition.MaterialInside, Allowance = allowance };
        var flange = doc.SheetMetal.MiterFlange(sketch, edges, spec)
            ?? throw FeatureFacts.NotMade(doc, What, $"from {profile.Name}", "check that the edges meet end to end and that the profile clears the rest of the part");
        return Flanges.Result(new[] { FeatureFacts.Made(references, doc, flange, tx) });
    }

    /// <summary>A miter flange with its bends at the radius and allowance asked, the volume grown within its profile's reach, and the part as long as a one-line profile.</summary>
    protected override void Check(ISwDocument doc, SheetMetalMiterFlangeRequest request, SheetMetalFeatureResult result)
    {
        var defaults = _defaults!;
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, "SMMiteredFlange", What);
        FeatureFacts.MadeFaces(probe, What);
        var bends = Flanges.BendsOf(doc, new[] { result.Feature.Name! });
        int expected = _bendsPerEdge * request.Edges.Count;
        if (bends.Count != expected)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"the miter flange made {bends.Count} bends, not the {expected} its profile gives along {request.Edges.Count} edges ({_bendsPerEdge - 1} corners and the bend at the sheet, per edge)",
                new JsonObject { ["check"] = "bends", ["expected"] = expected, ["measured"] = bends.Count });
        }
        var allowance = _allowance ?? defaults.Allowance;
        Flanges.BendsAre(bends, null, _radius, allowance);
        checks.Add(FormattableString.Invariant($"{bends.Count} bends ({_bendsPerEdge} per edge) at inner radius {_radius:0.####} mm and {allowance}"));
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureFacts.VolumeChanged(probe, What, VolumeBefore, after, 1);
        double t = defaults.ThicknessMm;
        double most = _edgesMm * t * (_profileMm + _bendsPerEdge * Math.PI * (_radius + t / 2));
        if (after - VolumeBefore > most * (1 + FeatureChecks.VolumeTolerance))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                FormattableString.Invariant($"the miter flange added {after - VolumeBefore:0.###} mm³, more than the {most:0.###} mm³ its {_profileMm:0.###} mm profile and {_bendsPerEdge} bends can hold along {_edgesMm:0.###} mm of edge"),
                new JsonObject { ["check"] = "volume", ["most"] = most, ["measured"] = after - VolumeBefore });
        }
        checks.Add(FormattableString.Invariant($"the volume grew by {after - VolumeBefore:0.###} mm³, within the {most:0.###} mm³ its profile and bends can hold along {_edgesMm:0.###} mm of edge"));
        Long(doc, t, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }

    /// <summary>A one-line profile square to the sheet: the part is now as long as the line that way, plus at most the thickness.</summary>
    void Long(ISwDocument doc, double t, List<string> checks)
    {
        var line = _line;
        var n = _first!.Normal;
        var box = doc.BoundingBox(true);
        if (line == null || box == null || _boxBefore == null || Math.Abs(FeatureFacts.Dot(line.Value.Direction, n)) < line.Value.LengthMm * (1 - 1e-6) || Flanges.Reach(box, n) == null)
        {
            checks.Add("the flange's length was not compared: its profile is not one line square to the sheet along x, y or z");
            return;
        }
        var flip = new Vector3(-n.X, -n.Y, -n.Z);
        double extent = Flanges.Reach(box, n)!.Value + Flanges.Reach(box, flip)!.Value;
        double before = Flanges.Reach(_boxBefore, n)!.Value + Flanges.Reach(_boxBefore, flip)!.Value;
        double length = line.Value.LengthMm;
        if (before > length)
        {
            checks.Add(FormattableString.Invariant($"the flange's length was not compared: the part was already {before:0.###} mm across that way"));
            return;
        }
        if (extent < length - 1e-3 || extent > length + t + 1e-3)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                FormattableString.Invariant($"the part is {extent:0.####} mm across along the profile's line after the miter flange, not between its {length:0.####} mm and that plus the {t:0.####} mm thickness"),
                new JsonObject { ["check"] = "length", ["expected"] = length, ["measured"] = extent });
        }
        checks.Add(FormattableString.Invariant($"the part is {extent:0.####} mm across along the {length:0.####} mm profile"));
    }
}
