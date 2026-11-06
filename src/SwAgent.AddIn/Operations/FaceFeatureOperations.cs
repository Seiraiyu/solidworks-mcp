using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class FeatureShellOperation : ModelChangeOperation<FeatureShellRequest, FeatureResult>
{
    public FeatureShellOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureShellRequest request) => request.Document;

    /// <summary>The faces to remove's areas before the shell, in order, for the gone check.</summary>
    List<double?> _areasBefore = new List<double?>();

    protected override FeatureResult Change(ISwDocument doc, FeatureShellRequest request, ModelTransaction tx, IOperationContext context)
    {
        double thickness = FeatureFacts.Length(request.ThicknessMm, "thickness_mm", "a shell");
        if (BodiesBefore == 0) throw FeatureFacts.Invalid("faces_to_remove", "a shell hollows a body, and this part has none yet");
        var faces = FeatureFacts.Entities(Services.References, doc, request.FacesToRemove, "faces_to_remove", ReferenceKind.Face);
        _areasBefore = faces.Select(f => doc.Describe(f).AreaMm2).ToList();
        var feature = doc.Shell(faces, thickness, request.Outward == true)
            ?? throw FeatureFacts.NotMade(doc, "shell", FormattableString.Invariant($"of {thickness:0.###} mm"), "check that the walls fit: a wall thicker than the smallest radius of curvature inside the body cannot be made");
        return FeatureFacts.Made(Services.References, doc, feature, tx);
    }

    /// <summary>The feature is a shell, the volume moved the way the walls go, the faces named are open, and it made faces.</summary>
    protected override void Check(ISwDocument doc, FeatureShellRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Shell", "shell");
        double after = Volume(doc, doc.SolidBodyCount());
        // The volume first: a face kept as its rim (below) counts as opened only once the volume has moved the right way.
        FeatureFacts.VolumeChanged(result, "shell", VolumeBefore, after, request.Outward == true ? 1 : -1);
        Opened(doc, request, result, FormattableString.Invariant($"the volume {VolumeBefore:0.###} -> {after:0.###} mm³"));
        FeatureFacts.MadeFaces(result, "shell");
    }

    /// <summary>
    /// Every face named in faces_to_remove is open: it no longer resolves, or (the shell's case alone, not
    /// FeatureFacts.Gone's) it resolves to a smaller area than it had. The 3.D live run showed SOLIDWORKS keeps a shell's
    /// removed face as the rim the walls leave of it: the block's 800 mm² top resolved to a 224 mm² rim, the volume
    /// exactly the open shell's 3392 mm³. A face of the same area or larger is still closed, and fails "gone" with what
    /// it resolves to.
    /// </summary>
    void Opened(ISwDocument doc, FeatureShellRequest request, FeatureResult result, string volume)
    {
        var texts = request.FacesToRemove;
        if (texts == null || texts.Count == 0) return;
        var cutBack = new List<string>();
        for (int i = 0; i < texts.Count; i++)
        {
            object face;
            try
            {
                face = Services.References.Resolve(doc, texts[i], $"/faces_to_remove/{i}", ReferenceKind.Face).Entity;
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved)
            {
                continue;
            }
            var now = doc.Describe(face);
            double? before = i < _areasBefore.Count ? _areasBefore[i] : null;
            if (before != null && now.AreaMm2 != null && now.AreaMm2 < before - 1e-6)
            {
                cutBack.Add(FormattableString.Invariant($"/faces_to_remove/{i}: {before:0.###} -> {now.AreaMm2:0.###} mm²"));
                continue;
            }
            string described = EntityFilter.Describe(now) + (now.Normal != null ? $", normal {now.Normal}" : "") + (now.Origin != null ? $", at {now.Origin}" : "");
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"/faces_to_remove/{i} is still a face of the part after the shell: it resolves to {described}; {volume}",
                new JsonObject { ["check"] = "gone", ["field"] = $"/faces_to_remove/{i}", ["measured"] = described });
        }
        result.Checks.Add(cutBack.Count == 0
            ? $"the {texts.Count} faces named in faces_to_remove are gone"
            : $"the {texts.Count} faces named in faces_to_remove are gone or cut back to the walls' rim ({string.Join("; ", cutBack)})");
    }
}

public sealed class FeatureDraftOperation : ModelChangeOperation<FeatureDraftRequest, FeatureResult>
{
    public FeatureDraftOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(FeatureDraftRequest request) => request.Document;

    /// <summary>How far a drafted face may stand from 90° less the draft to the neutral normal, degrees.</summary>
    public const double AngleToleranceDeg = 1e-4;

    protected override FeatureResult Change(ISwDocument doc, FeatureDraftRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.AngleDeg == null || !(request.AngleDeg > 0 && request.AngleDeg < 90)) throw FeatureFacts.Invalid("angle_deg", "angle_deg must be given, more than 0 and less than 90 degrees");
        if (request.Faces.Count == 0) throw FeatureFacts.Invalid("faces", "a draft needs the faces to taper");
        var references = Services.References;
        var (neutral, _) = references.Resolve(doc, request.NeutralPlane, "neutral_plane", ReferenceKind.Plane, ReferenceKind.Face);
        NeutralNormal(doc, neutral);
        var faces = FeatureFacts.Entities(references, doc, request.Faces, "faces", ReferenceKind.Face);
        int same = faces.FindIndex(f => ReferenceEquals(f, neutral));
        if (same >= 0) throw FeatureFacts.Invalid($"/faces/{same}", $"/faces/{same} is the neutral plane itself; it keeps its size and is not drafted");
        var spec = new DraftSpec { Neutral = neutral, AngleDeg = request.AngleDeg.Value, Reverse = request.Reverse == true };
        spec.Faces.AddRange(faces);
        var feature = doc.Draft(spec)
            ?? throw FeatureFacts.NotMade(doc, "draft", FormattableString.Invariant($"of {spec.AngleDeg:0.###}°"), "check that every face meets the neutral plane and that the angle leaves the faces' neighbours room");
        return FeatureFacts.Made(references, doc, feature, tx);
    }

    /// <summary>The neutral plane's normal; invalid_argument when it is a face that is not planar.</summary>
    static Vector3 NeutralNormal(ISwDocument doc, object neutral)
    {
        var info = doc.Describe(neutral);
        if (info.Geometry != GeometryType.Plane || info.Normal == null)
            throw FeatureFacts.Invalid("neutral_plane", $"neutral_plane: a draft's neutral plane is a plane or a planar face, and this is a {WireNames.ForEnum(info.Geometry)}");
        return info.Normal;
    }

    /// <summary>The feature is a draft, the volume changed, and every planar face it drafted stands at 90° less the draft to the neutral normal.</summary>
    protected override void Check(ISwDocument doc, FeatureDraftRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "Draft", "draft");
        double after = Volume(doc, doc.SolidBodyCount());
        FeatureChecks.VolumeMoved("draft", VolumeBefore, after);
        result.Checks.Add(FormattableString.Invariant($"the volume changed: {VolumeBefore:0.###} -> {after:0.###} mm³"));
        // No MadeFaces: a draft tilts faces in place, and SOLIDWORKS lists none as the draft's own (3.D live run). The
        // faces it tilted are measured below, and a draft with none to measure fails there.
        var (neutral, _) = Services.References.Resolve(doc, request.NeutralPlane, "neutral_plane", ReferenceKind.Plane, ReferenceKind.Face);
        var normal = NeutralNormal(doc, neutral);
        double expected = 90 - request.AngleDeg!.Value;
        int measured = 0, gone = 0;
        for (int i = 0; i < request.Faces.Count; i++)
        {
            object face;
            try
            {
                face = Services.References.Resolve(doc, request.Faces[i], $"/faces/{i}", ReferenceKind.Face).Entity;
            }
            catch (SwAgentException e) when (e.Code == ErrorCode.ReferenceUnresolved)
            {
                gone++;
                continue; // SOLIDWORKS made the drafted face anew; the draft's own faces are measured below
            }
            var info = doc.Describe(face);
            if (info.Geometry != GeometryType.Plane || info.Normal == null) continue;
            FeatureChecks.Near("draft_angle", $"/faces/{i}'s angle to the neutral plane's normal", expected, FeatureChecks.AngleDeg(info.Normal, normal), AngleToleranceDeg, "°");
            measured++;
        }
        if (measured == 0)
        {
            var planar = result.Faces.Where(f => f.Geometry == GeometryType.Plane && f.Normal != null).ToList();
            if (planar.Count > 0 && !planar.Any(f => Math.Abs(FeatureChecks.AngleDeg(f.Normal!, normal) - expected) <= AngleToleranceDeg))
            {
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    FormattableString.Invariant($"no face of {result.Feature.Name} stands at {expected:0.###}° to the neutral plane's normal"),
                    new JsonObject { ["check"] = "draft_angle", ["expected"] = expected, ["measured"] = planar.Select(f => (object?)FeatureChecks.AngleDeg(f.Normal!, normal)).ToList() });
            }
            measured = planar.Count;
        }
        if (gone == request.Faces.Count && result.Faces.Count == 0)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"no planar face {result.Feature.Name} drafted could be measured: the faces named no longer resolve and the draft lists none of its own",
                new JsonObject { ["check"] = "draft_angle", ["expected"] = expected, ["measured"] = 0 });
        }
        result.Checks.Add(FormattableString.Invariant($"{measured} drafted planar faces stand at {expected:0.###}° to the neutral plane's normal"));
    }
}

public sealed class FeatureHoleOperation : ModelChangeOperation<FeatureHoleRequest, FeatureResult>
{
    public FeatureHoleOperation(AddInServices services)
        : base(services)
    {
    }

    /// <summary>How far a position may be from the face, and a hole's axis from its position, mm.</summary>
    public const double PositionToleranceMm = 0.01;

    protected override string? DocumentOf(FeatureHoleRequest request) => request.Document;

    protected override FeatureResult Change(ISwDocument doc, FeatureHoleRequest request, ModelTransaction tx, IOperationContext context)
    {
        var spec = Spec(request);
        if (BodiesBefore == 0) throw FeatureFacts.Invalid("face", "a hole is drilled into a body, and this part has none yet");
        var (face, _) = Services.References.Resolve(doc, request.Face, "face", ReferenceKind.Face);
        var info = doc.Describe(face);
        if (info.Geometry != GeometryType.Plane)
            throw FeatureFacts.Invalid("face", $"face: Hole Wizard holes start on a planar face, and this is a {WireNames.ForEnum(info.Geometry)}");
        for (int i = 0; i < request.Positions.Count; i++)
        {
            double off = doc.DistanceMm(face, request.Positions[i]);
            if (off > PositionToleranceMm)
                throw FeatureFacts.Invalid($"/positions/{i}", FormattableString.Invariant($"/positions/{i} {request.Positions[i]} is {off:0.###} mm off the face; give points on it"));
        }
        spec.Face = face;
        var editor = doc.NewSketch(face);
        var sketch = new CreatedItem { Name = editor.Name, Kind = ReferenceKind.Sketch };
        tx.Made(sketch, () =>
        {
            doc.DeleteFeature(editor.Sketch);
            doc.Rebuild(false);
        });
        try
        {
            foreach (var p in request.Positions)
            {
                spec.Points.Add(editor.Point(editor.ToSketch(p)));
                spec.Positions.Add(p);
            }
        }
        finally
        {
            editor.Close();
        }
        sketch.Reference = Services.References.Describe(doc, editor.Sketch).Reference;
        string what = $"{WireNames.ForEnum(spec.Type)} {spec.Size} hole";
        var feature = doc.Hole(spec)
            ?? throw FeatureFacts.NotMade(doc, what, $"at {spec.Positions.Count} positions", $"check that {spec.Size} is a {WireNames.ForEnum(spec.Standard)} size the Hole Wizard lists for a {WireNames.ForEnum(spec.Type)} hole");
        var made = FeatureFacts.Made(Services.References, doc, feature, tx);
        if (spec.How != null) made.Checks.Add($"placed with {spec.How}");
        return made;
    }

    /// <summary>The request as SOLIDWORKS takes it, every field checked: invalid_argument names the first that is wrong.</summary>
    static HoleSpec Spec(FeatureHoleRequest r)
    {
        if (r.Positions.Count == 0) throw FeatureFacts.Invalid("positions", "a hole needs at least one position on the face");
        for (int i = 0; i < r.Positions.Count; i++)
        {
            int seen = r.Positions.Take(i).ToList().FindIndex(p => Distance(p, r.Positions[i]) <= PositionToleranceMm);
            if (seen >= 0) throw FeatureFacts.Invalid($"/positions/{i}", $"/positions/{i} is where /positions/{seen} is");
        }
        if (string.IsNullOrWhiteSpace(r.Size)) throw FeatureFacts.Invalid("size", "a hole needs its size as the Hole Wizard lists it, for example M5 or Ø5.0");
        var end = r.End ?? HoleEnd.Blind;
        var spec = new HoleSpec { Type = r.Type ?? HoleType.Simple, Standard = r.Standard ?? HoleStandard.Iso, Size = r.Size.Trim(), End = end };
        if (end == HoleEnd.Blind) spec.DepthMm = FeatureFacts.Length(r.DepthMm, "depth_mm", "a blind hole");
        else if (r.DepthMm != null) throw FeatureFacts.Invalid("depth_mm", "depth_mm is for a blind hole, and this one goes through_all");
        return spec;
    }

    static double Distance(Point3 a, Point3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>How far <paramref name="p"/> is from the line through <paramref name="o"/> along <paramref name="axis"/>, mm.</summary>
    public static double FromAxis(Point3 p, Point3 o, Vector3 axis)
    {
        double ax = axis.X, ay = axis.Y, az = axis.Z, l = Math.Sqrt(ax * ax + ay * ay + az * az);
        double dx = p.X - o.X, dy = p.Y - o.Y, dz = p.Z - o.Z;
        double cx = dy * az - dz * ay, cy = dz * ax - dx * az, cz = dx * ay - dy * ax;
        return Math.Sqrt(cx * cx + cy * cy + cz * cz) / l;
    }

    /// <summary>The feature is a Hole Wizard hole, the volume shrank, and there is one hole (a set of coaxial cylinders) on each position's axis.</summary>
    protected override void Check(ISwDocument doc, FeatureHoleRequest request, FeatureResult result)
    {
        FeatureFacts.ExpectType(doc, result, "HoleWzd", "hole");
        FeatureFacts.VolumeChanged(result, "hole", VolumeBefore, Volume(doc, doc.SolidBodyCount()), -1);
        var cylinders = result.Faces.Where(f => f.Geometry == GeometryType.Cylinder && f.Axis != null && f.Origin != null).ToList();
        var axes = new List<EntityInfo>();
        foreach (var c in cylinders)
        {
            if (!axes.Any(a => FeatureChecks.AngleDeg(a.Axis!, c.Axis!) < 1e-3 && FromAxis(c.Origin!, a.Origin!, a.Axis!) <= PositionToleranceMm)) axes.Add(c);
        }
        for (int i = 0; i < request.Positions.Count; i++)
        {
            var p = request.Positions[i];
            if (!axes.Any(a => FromAxis(p, a.Origin!, a.Axis!) <= PositionToleranceMm))
            {
                double nearest = axes.Count == 0 ? double.PositiveInfinity : axes.Min(a => FromAxis(p, a.Origin!, a.Axis!));
                throw new SwAgentException(ErrorCode.VerificationFailed,
                    FormattableString.Invariant($"no hole of {result.Feature.Name} is on /positions/{i} {p}; the nearest axis is {nearest:0.###} mm off it"),
                    new JsonObject { ["check"] = "hole_position", ["field"] = $"/positions/{i}", ["expected"] = 0.0, ["measured"] = double.IsInfinity(nearest) ? null : (object?)nearest });
            }
        }
        if (axes.Count != request.Positions.Count)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"{result.Feature.Name} made {axes.Count} holes for {request.Positions.Count} positions",
                new JsonObject { ["check"] = "hole_count", ["expected"] = request.Positions.Count, ["measured"] = axes.Count });
        }
        result.Checks.Add($"{axes.Count} holes, one on each position's axis");
    }
}
