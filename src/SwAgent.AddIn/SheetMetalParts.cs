using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// What every sheetmetal operation shares (design 454, Sheet-metal parts): what a sheet-metal part is, its state, the
/// rules (sheet metal, folded to change), the allowance and relief a request gives, and the facts read back after a
/// change. SOLIDWORKS' type names: SheetMetal for the part's sheet-metal feature, FlatPattern for its flat pattern.
/// </summary>
public static class SheetMetalParts
{
    public const string SheetMetalType = "SheetMetal";
    public const string FlatPatternType = "FlatPattern";

    /// <summary>The cut-list properties that give the flat pattern's size (SOLIDWORKS 2026's names, measured by CADapter).</summary>
    public const string LengthProperty = "Bounding Box Length";
    public const string WidthProperty = "Bounding Box Width";

    /// <summary>A part is sheet metal when its tree has a SheetMetal feature.</summary>
    public static bool IsSheetMetal(ISwDocument doc) => doc.Features().Any(f => f.Type == SheetMetalType);

    /// <summary>Flattened when the part's FlatPattern feature is unsuppressed (design 454: never SetBendState or GetBendState).</summary>
    public static SheetState State(ISwDocument doc) =>
        doc.Features().Any(f => f.Type == FlatPatternType && !f.Suppressed) ? SheetState.Flattened : SheetState.Folded;

    /// <summary>The part's defaults; wrong_document_type, with needs sheet_metal, when it is not sheet metal.</summary>
    public static SheetDefaults Require(ISwDocument doc, string operation)
    {
        var defaults = IsSheetMetal(doc) ? doc.SheetMetal.Defaults() : null;
        if (defaults != null) return defaults;
        var info = doc.Info();
        throw new SwAgentException(ErrorCode.WrongDocumentType,
            $"{operation} works on sheet-metal parts, and {info.Title} is not one; start one with sw_sheetmetal_base_flange",
            new JsonObject { ["document"] = DocumentLocator.Label(info), ["type"] = "part", ["needs"] = "sheet_metal" });
    }

    /// <summary>invalid_argument when the part is flattened: a change is made to the folded part, and nothing is folded on the agent's behalf.</summary>
    public static void RequireFolded(ISwDocument doc, string operation)
    {
        if (State(doc) == SheetState.Folded) return;
        throw new SwAgentException(ErrorCode.InvalidArgument,
            $"{operation} changes a folded part, and {doc.Info().Title} is flattened; fold it (turn its flat pattern off) first. Nothing was changed",
            new JsonObject { ["field"] = "document", ["state"] = "flattened" });
    }

    /// <summary>The allowance given, checked: exactly one of its three fields, each in range. Fields under <paramref name="field"/>.</summary>
    public static BendAllowance Allowance(BendAllowance given, string field)
    {
        int count = (given.KFactor != null ? 1 : 0) + (given.AllowanceMm != null ? 1 : 0) + (given.DeductionMm != null ? 1 : 0);
        if (count != 1) throw FeatureFacts.Invalid(field, $"{field} needs exactly one of k_factor, allowance_mm or deduction_mm, and it has {count}");
        if (given.KFactor is double k && (double.IsNaN(k) || k <= 0 || k > 1))
            throw FeatureFacts.Invalid($"{field}/k_factor", $"k_factor must be more than 0 and at most 1, not {k.ToString(CultureInfo.InvariantCulture)}");
        if (given.AllowanceMm != null) FeatureFacts.Length(given.AllowanceMm, $"{field}/allowance_mm", "a bend allowance");
        if (given.DeductionMm is double d && (double.IsNaN(d) || double.IsInfinity(d) || d < 0 || d > SketchDraft.Reach))
            throw FeatureFacts.Invalid($"{field}/deduction_mm", $"deduction_mm must be 0 or more and at most {SketchDraft.Reach} mm, not {d.ToString(CultureInfo.InvariantCulture)}");
        return new BendAllowance { KFactor = given.KFactor, AllowanceMm = given.AllowanceMm, DeductionMm = given.DeductionMm };
    }

    /// <summary>A relief ratio: at least 0.05 and at most 2, as SOLIDWORKS takes it.</summary>
    public static double ReliefRatio(double? ratio, string field)
    {
        double r = ratio ?? 0.5;
        if (double.IsNaN(r) || r < 0.05 || r > 2) throw FeatureFacts.Invalid(field, $"{field} must be at least 0.05 and at most 2, not {r.ToString(CultureInfo.InvariantCulture)}");
        return r;
    }

    /// <summary>Two allowances the same, within a millionth.</summary>
    public static bool Same(BendAllowance a, BendAllowance b) =>
        Near(a.KFactor, b.KFactor) && Near(a.AllowanceMm, b.AllowanceMm) && Near(a.DeductionMm, b.DeductionMm);

    static bool Near(double? a, double? b) => a == null ? b == null : b != null && Math.Abs(a.Value - b.Value) <= 1e-6;

    /// <summary>The bends as an agent reads them: references, and each one's width along its axis from its cylindrical faces.</summary>
    public static List<BendInfo> Describe(ReferenceService references, ISwDocument doc, IEnumerable<BendRecord> bends) =>
        bends.Select(b => new BendInfo
        {
            Name = b.Name,
            Reference = references.Describe(doc, b.Feature).Reference,
            Feature = b.Owner,
            AngleDeg = b.AngleDeg,
            InnerRadiusMm = b.RadiusMm,
            Allowance = b.Allowance,
            DefaultRadius = b.DefaultRadius,
            DefaultAllowance = b.DefaultAllowance,
            WidthMm = Width(doc, b),
        }).ToList();

    /// <summary>The extent of the bend's cylindrical faces along their axis, when that axis is along x, y or z.</summary>
    static double? Width(ISwDocument doc, BendRecord bend)
    {
        var cylinders = doc.FacesOf(bend.Feature).Select(doc.Describe).Where(i => i.Geometry == GeometryType.Cylinder && i.Axis != null && i.Box != null).ToList();
        if (cylinders.Count == 0) return null;
        var axis = cylinders[0].Axis!;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var c in cylinders)
        {
            var box = c.Box!;
            if (Math.Abs(axis.X) > 1 - 1e-9) { lo = Math.Min(lo, box.Min.X); hi = Math.Max(hi, box.Max.X); }
            else if (Math.Abs(axis.Y) > 1 - 1e-9) { lo = Math.Min(lo, box.Min.Y); hi = Math.Max(hi, box.Max.Y); }
            else if (Math.Abs(axis.Z) > 1 - 1e-9) { lo = Math.Min(lo, box.Min.Z); hi = Math.Max(hi, box.Max.Z); }
            else return null;
        }
        return hi - lo;
    }

    /// <summary>The thickness, state and the bends given, as a result's sheet-metal facts.</summary>
    public static SheetMetalFacts Facts(ReferenceService references, ISwDocument doc, IEnumerable<BendRecord> bends) => new SheetMetalFacts
    {
        ThicknessMm = doc.SheetMetal.Defaults()?.ThicknessMm ?? 0,
        State = State(doc),
        Bends = Describe(references, doc, bends),
    };

    /// <summary>The sheet's largest planar face (its base face), or null when its body has no planar face.</summary>
    public static object? BaseFace(ISwDocument doc) =>
        doc.SheetMetal.SheetFaces().Select(f => (Face: f, Info: doc.Describe(f))).Where(f => f.Info.Geometry == GeometryType.Plane)
            .OrderByDescending(f => f.Info.AreaMm2 ?? 0).Select(f => f.Face).FirstOrDefault();

    /// <summary>
    /// The sheet's thickness as its geometry has it: the distance from its base face to the largest planar face facing
    /// the other way. Null when there is no such pair.
    /// </summary>
    public static double? MeasuredThickness(ISwDocument doc)
    {
        var planar = doc.SheetMetal.SheetFaces().Select(f => (Face: f, Info: doc.Describe(f))).Where(f => f.Info.Geometry == GeometryType.Plane && f.Info.Normal != null)
            .OrderByDescending(f => f.Info.AreaMm2 ?? 0).ToList();
        if (planar.Count < 2) return null;
        var top = planar[0];
        var opposite = planar.Skip(1).FirstOrDefault(f => FeatureFacts.Dot(f.Info.Normal!, top.Info.Normal!) < -1 + 1e-6);
        if (opposite.Face == null) return null;
        return doc.ClosestDistance(top.Face, opposite.Face, out _, out _);
    }

    /// <summary>
    /// The thickness reads back as <paramref name="expected"/> from the SheetMetal feature, and the geometry measures it
    /// too where it can; both are recorded in the result's checks.
    /// </summary>
    public static void ThicknessIs(ISwDocument doc, double expected, List<string> checks)
    {
        double read = doc.SheetMetal.Defaults()?.ThicknessMm ?? 0;
        FeatureChecks.Near("thickness", "the sheet's thickness as SOLIDWORKS reads it back", expected, read, 1e-6, "mm");
        double? measured = MeasuredThickness(doc);
        if (measured == null)
        {
            checks.Add(FormattableString.Invariant($"the thickness reads back as {read:0.####} mm (no pair of opposite faces to measure it between)"));
            return;
        }
        FeatureChecks.Near("thickness_measured", "the distance between the sheet's base face and the face opposite it", expected, measured.Value, 1e-4, "mm");
        checks.Add(FormattableString.Invariant($"the thickness reads back as {read:0.####} mm and measures {measured:0.####} mm between the sheet's two faces"));
    }

    /// <summary>
    /// The flat pattern's size from the cut list (Bounding Box Length and Width, in the document's units), mm; null
    /// when the cut list does not give both as numbers.
    /// </summary>
    public static (double LengthMm, double WidthMm)? FlatSize(IReadOnlyList<KeyValuePair<string, string>> cutList, double mmPerUnit)
    {
        double? Number(string name)
        {
            var found = cutList.FirstOrDefault(p => p.Key == name);
            return found.Key != null && double.TryParse(found.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v * mmPerUnit : (double?)null;
        }
        double? length = Number(LengthProperty), width = Number(WidthProperty);
        return length == null || width == null ? ((double, double)?)null : (length.Value, width.Value);
    }

    /// <summary>The flat size as a result gives it, with the bends the cut list counts (or <paramref name="bends"/> when it does not); null without a size.</summary>
    public static FlatSize? Flat(IReadOnlyList<KeyValuePair<string, string>> cutList, double mmPerUnit, int bends)
    {
        var size = FlatSize(cutList, mmPerUnit);
        if (size == null) return null;
        var counted = cutList.FirstOrDefault(p => p.Key == "Bends");
        int n = counted.Key != null && double.TryParse(counted.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? (int)Math.Round(v) : bends;
        return new FlatSize { LengthMm = size.Value.LengthMm, WidthMm = size.Value.WidthMm, Bends = n };
    }

    /// <summary>
    /// The edge a flange or hem is made on, read as the sheet has it: a straight edge between one of the sheet's faces
    /// (its normal is where a flange rises) and the thin face the sheet ends in (its normal points away from the
    /// material, and it is as wide as the sheet is thick). invalid_argument at <paramref name="field"/> otherwise.
    /// </summary>
    public static SheetEdge Edge(ISwDocument doc, object edge, string field, double thicknessMm)
    {
        var info = doc.Describe(edge);
        if (info.Geometry != GeometryType.Line || info.Origin == null || info.Axis == null || info.LengthMm == null)
            throw FeatureFacts.Invalid(field, $"{field} is a {WireNames.ForEnum(info.Geometry)} edge; a flange or hem is made on a straight edge of the sheet");
        var axis = info.Axis;
        var planar = doc.SheetMetal.AdjacentFaces(edge).Select(f => (Face: f, Info: doc.Describe(f)))
            .Where(f => f.Info.Geometry == GeometryType.Plane && f.Info.Normal != null && Math.Abs(FeatureFacts.Dot(f.Info.Normal, axis)) < 1e-6).ToList();
        double end = info.LengthMm.Value * thicknessMm;
        var thin = planar.FirstOrDefault(f => f.Info.AreaMm2 is double a && Math.Abs(a - end) <= 1e-3 * end + 1e-6);
        var sheet = thin.Face == null ? default : planar.FirstOrDefault(f => !ReferenceEquals(f.Face, thin.Face) && Math.Abs(FeatureFacts.Dot(f.Info.Normal!, thin.Info.Normal!)) < 1e-6);
        if (thin.Face == null || sheet.Face == null)
        {
            throw FeatureFacts.Invalid(field,
                $"{field} is not an edge where the sheet ends: a flange or hem is made on a straight edge between one of the sheet's faces and the thin face, {thicknessMm.ToString("0.####", CultureInfo.InvariantCulture)} mm wide, it ends in (sheetmetal.read gives the base face; model.find gives its edges)");
        }
        var (start, finish) = FlangeGeometry.Ends(info.Origin, axis, info.LengthMm.Value);
        return new SheetEdge(edge, start, finish, info.LengthMm.Value, sheet.Face, sheet.Info.Normal!, sheet.Info.Origin, thin.Info.Normal!);
    }

    /// <summary>
    /// How far rounding can have put the cut list's length plus width off, mm: half a unit in the last decimal place
    /// each is written to (SOLIDWORKS rounds them to the part's display precision; CADapter read 83.28 for 83.2832).
    /// </summary>
    public static double FlatRoundingMm(IReadOnlyList<KeyValuePair<string, string>> cutList, double mmPerUnit)
    {
        double Half(string name)
        {
            string value = cutList.FirstOrDefault(p => p.Key == name).Value?.Trim() ?? "";
            int dot = value.IndexOf('.');
            return 0.5 * Math.Pow(10, -(dot < 0 ? 0 : value.Length - dot - 1)) * mmPerUnit;
        }
        return Half(LengthProperty) + Half(WidthProperty);
    }
}

/// <summary>An edge of the sheet as <see cref="SheetMetalParts.Edge"/> reads it. Millimetres.</summary>
public sealed class SheetEdge
{
    public SheetEdge(object edge, Point3 start, Point3 end, double lengthMm, object face, Vector3 normal, Point3? faceOrigin, Vector3 outward)
    {
        Entity = edge;
        Start = start;
        End = end;
        LengthMm = lengthMm;
        Face = face;
        Normal = normal;
        FaceOrigin = faceOrigin;
        Outward = outward;
    }

    public object Entity { get; }
    public Point3 Start { get; }
    public Point3 End { get; }
    public double LengthMm { get; }

    /// <summary>The sheet's face the edge bounds.</summary>
    public object Face { get; }

    /// <summary>That face's outward normal: the way a flange rises.</summary>
    public Vector3 Normal { get; }

    /// <summary>A point on that face (its centre), for its level along the normal.</summary>
    public Point3? FaceOrigin { get; }

    /// <summary>The thin face's normal: in the face's plane, square to the edge, away from the material.</summary>
    public Vector3 Outward { get; }
}
