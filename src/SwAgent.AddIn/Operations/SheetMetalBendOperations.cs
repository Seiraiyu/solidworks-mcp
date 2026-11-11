using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

public sealed class SheetMetalSketchedBendOperation : ModelChangeOperation<SheetMetalSketchedBendRequest, SheetMetalFeatureResult>
{
    const string What = "sketched bend";

    /// <summary>SOLIDWORKS' type names a sketched bend may read back as (the live run records which).</summary>
    static readonly string[] Types = { "SM3dBend", "SketchBend" };

    SheetDefaults? _defaults;
    FixedPlane? _fixed;
    int _lines;
    double _angle;
    double _radius;
    BendAllowance? _allowance;

    public SheetMetalSketchedBendOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(SheetMetalSketchedBendRequest request) => request.Document;

    protected override SheetMetalFeatureResult Change(ISwDocument doc, SheetMetalSketchedBendRequest request, ModelTransaction tx, IOperationContext context)
    {
        var defaults = SheetMetalParts.Require(doc, Name);
        SheetMetalParts.RequireFolded(doc, Name);
        double angle = Flanges.Angle(request.AngleDeg, 90, "angle_deg", 180);
        double? radius = request.BendRadiusMm == null ? null : FeatureFacts.Length(request.BendRadiusMm, "bend_radius_mm", "a bend");
        var allowance = request.Allowance == null ? null : SheetMetalParts.Allowance(request.Allowance, "allowance");
        var references = Services.References;
        var (sketch, _) = references.Resolve(doc, request.Sketch, "sketch", ReferenceKind.Sketch);
        var (face, _) = references.Resolve(doc, request.FixedFace, "fixed_face", ReferenceKind.Face);
        var plane = SheetMetalParts.Fixed(doc, face, "fixed_face");
        var profile = FeatureFacts.Profile(doc, sketch, "sketch", What);
        var lines = profile.Segments.Where(s => !s.Construction).ToList();
        if (lines.Any(s => s.Kind != SketchSegmentKind.Line || s.Start == null || s.End == null))
            throw FeatureFacts.Invalid("sketch", $"{profile.Name} has more than lines: each line of a sketched bend's sketch is one bend (construction lines are ignored)");
        var o = profile.Origin;
        double off = FeatureFacts.Dot(plane.Normal, new Vector3(o.X - plane.Centre.X, o.Y - plane.Centre.Y, o.Z - plane.Centre.Z));
        if (Math.Abs(FeatureFacts.Dot(profile.Normal, plane.Normal)) < 1 - 1e-6 || Math.Abs(off) > 1e-6)
            throw FeatureFacts.Invalid("sketch", $"{profile.Name} is not on fixed_face's plane: draw the bend lines on the face that stays put (sketch.create on it)");
        var c = plane.Centre;
        var centre = new Point2(
            (c.X - o.X) * profile.XAxis.X + (c.Y - o.Y) * profile.XAxis.Y + (c.Z - o.Z) * profile.XAxis.Z,
            (c.X - o.X) * profile.YAxis.X + (c.Y - o.Y) * profile.YAxis.Y + (c.Z - o.Z) * profile.YAxis.Z);
        for (int i = 0; i < lines.Count; i++)
        {
            double d = Across(lines[i].Start!, lines[i].End!, centre);
            if (d <= 1e-3)
                throw FeatureFacts.Invalid("fixed_face", $"fixed_face's centre lies on the line {lines[i].Name} of {profile.Name}: the side of the lines holding its centre stays put, so it must be off every line");
        }
        _defaults = defaults;
        _fixed = plane;
        _lines = lines.Count;
        _angle = angle;
        _radius = radius ?? defaults.BendRadiusMm;
        _allowance = allowance;
        var spec = new SketchedBendSpec { AngleDeg = angle, RadiusMm = radius, Position = request.Position ?? BendPosition.BendOutside, Flip = request.Flip == true, Allowance = allowance };
        var bend = doc.SheetMetal.SketchedBend(sketch, plane.Centre, spec)
            ?? throw FeatureFacts.NotMade(doc, What, $"from {profile.Name}", "check that its lines cross the face, that no line crosses a flange, and that each bend clears the rest of the part");
        return Flanges.Result(new[] { FeatureFacts.Made(references, doc, bend, tx) });
    }

    /// <summary>How far <paramref name="p"/> is from the line through <paramref name="a"/> and <paramref name="b"/>, mm.</summary>
    static double Across(Point2 a, Point2 b, Point2 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length < 1e-9 ? Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) : Math.Abs(dx * (p.Y - a.Y) - dy * (p.X - a.X)) / length;
    }

    /// <summary>A bend per line at the angle, radius and allowance asked, the thickness unchanged, the volume as the bend arithmetic folds it, and the fixed face put.</summary>
    protected override void Check(ISwDocument doc, SheetMetalSketchedBendRequest request, SheetMetalFeatureResult result)
    {
        var defaults = _defaults!;
        var checks = result.Checks;
        var probe = new FeatureResult { Feature = result.Feature, Faces = result.Faces, Checks = checks };
        FeatureFacts.ExpectType(doc, probe, Types, What);
        FeatureFacts.MadeFaces(probe, What);
        var bends = Flanges.BendsOf(doc, new[] { result.Feature.Name! });
        if (bends.Count != _lines)
        {
            throw new SwAgentException(ErrorCode.VerificationFailed, $"the sketched bend made {bends.Count} bends from {_lines} lines, not one per line",
                new JsonObject { ["check"] = "bends", ["expected"] = _lines, ["measured"] = bends.Count });
        }
        var allowance = _allowance ?? defaults.Allowance;
        Flanges.BendsAre(bends, _angle, _radius, allowance);
        checks.Add(FormattableString.Invariant($"{bends.Count} bends, one per line, at {_angle:0.###}°, inner radius {_radius:0.####} mm and {allowance}"));
        SheetMetalParts.ThicknessIs(doc, defaults.ThicknessMm, checks);
        double after = Volume(doc, doc.SolidBodyCount());
        SheetMetalParts.VolumeDeveloped(BendDevelopment.Develop(SheetMetalParts.Developed(doc, bends), defaults.ThicknessMm, flatten: false), VolumeBefore, after, What, checks);
        SheetMetalParts.StaysPut(doc, _fixed!, What, checks);
        result.Sheet = SheetMetalParts.Facts(Services.References, doc, bends);
    }
}
