using System.Collections.Generic;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// A part's sheet metal, as the sheetmetal operations read and change it (design 454), so they can be tested without
/// SOLIDWORKS. UI thread only. Millimetres and degrees; SOLIDWORKS' entities and features passed around as object.
/// </summary>
public interface ISwSheetMetal
{
    /// <summary>The part's sheet-metal defaults from its SheetMetal feature, or null when it has none.</summary>
    SheetDefaults? Defaults();

    /// <summary>How many of the part's solid bodies are sheet metal (IBody2.IsSheetMetal).</summary>
    int SheetBodyCount();

    /// <summary>The faces of the part's sheet-metal body; empty when it has none.</summary>
    IReadOnlyList<object> SheetFaces();

    /// <summary>Every bend of the part (a sub-feature whose definition is IOneBendFeatureData), in tree order.</summary>
    IReadOnlyList<BendRecord> Bends();

    /// <summary>A base flange from <paramref name="sketch"/>; the new flange feature, or null when SOLIDWORKS made none. Selects what it needs and clears the selection.</summary>
    object? BaseFlange(object sketch, BaseFlangeSpec spec);

    /// <summary>
    /// Writes the part's defaults that <paramref name="edit"/> gives (a null field is left alone): the thickness through
    /// the base flange, the bend radius, allowance and relief through the SheetMetal feature. Does not rebuild. What
    /// SOLIDWORKS answered is not evidence (design 454: ModifyDefinition can say true and change nothing); the caller
    /// reads the part back.
    /// </summary>
    void SetDefaults(SheetDefaultsEdit edit);

    /// <summary>
    /// The fallback for a K-factor default (design 454: the global K-factor was measured not reaching edge-flange bends):
    /// a flange (base, edge, miter or hem) whose own bends follow the part's default (SOLIDWORKS says so, or they read <paramref name="oldK"/>)
    /// but did not take <paramref name="k"/> gets it written into its own definition, with its K-factor override on.
    /// Nothing is written where the default reached them. Returns each flange it wrote, with what it held before and the
    /// bends that missed, so the caller can record it and put it back. Does not rebuild.
    /// </summary>
    IReadOnlyList<FlangeKFactorWrite> KFactorToBaseFlanges(double k, double? oldK);

    /// <summary>Writes a flange's own K-factor override switch and K-factor (the fallback's rollback). Does not rebuild.</summary>
    void SetBaseFlangeKFactor(string flange, bool overrideKFactor, double kFactor);

    /// <summary>The sheet-metal body's cut-list properties, each name with its resolved value, after updating the cut list; empty when there is none.</summary>
    IReadOnlyList<KeyValuePair<string, string>> CutList();

    /// <summary>How many millimetres one of the document's length units is (the unit cut-list values are written in).</summary>
    double MmPerUnit();

    /// <summary>The faces on either side of an edge (IEdge.GetTwoAdjacentFaces2).</summary>
    IReadOnlyList<object> AdjacentFaces(object edge);

    /// <summary>
    /// The empty sketch SOLIDWORKS makes for an edge flange on <paramref name="edge"/> at <paramref name="angleDeg"/>
    /// (IModelDoc2.InsertSketchForEdgeFlange), closed; null when it made none. Its profile is drawn by the caller: an
    /// empty one makes a flange of no size, with no error (design 454).
    /// </summary>
    object? EdgeFlangeSketch(object edge, double angleDeg);

    /// <summary>An edge flange on <paramref name="edge"/> from its sketch's profile; the new feature, or null when SOLIDWORKS made none. Clears the selection.</summary>
    object? EdgeFlange(object edge, object sketch, EdgeFlangeSpec spec);

    /// <summary>A miter flange of <paramref name="sketch"/>'s open profile along <paramref name="edges"/>; the new feature, or null when SOLIDWORKS made none. Selects what it needs and clears the selection.</summary>
    object? MiterFlange(object sketch, IReadOnlyList<object> edges, MiterFlangeSpec spec);

    /// <summary>A hem on <paramref name="edges"/>; the new feature, or null when SOLIDWORKS made none. Selects what it needs and clears the selection.</summary>
    object? Hem(IReadOnlyList<object> edges, HemSpec spec);
}

/// <summary>An edge flange as ISwSheetMetal.EdgeFlange makes it.</summary>
public sealed class EdgeFlangeSpec
{
    public double AngleDeg { get; set; } = 90;

    /// <summary>Its inner radius; null for the part's default.</summary>
    public double? RadiusMm { get; set; }

    public BendPosition Position { get; set; } = BendPosition.MaterialInside;

    /// <summary>Where SOLIDWORKS dimensions its length from (the profile, not this, sets the length).</summary>
    public FlangeLengthFrom LengthFrom { get; set; } = FlangeLengthFrom.OuterVirtualSharp;

    /// <summary>Its own allowance; null for the part's default.</summary>
    public BendAllowance? Allowance { get; set; }
}

/// <summary>A miter flange as ISwSheetMetal.MiterFlange makes it.</summary>
public sealed class MiterFlangeSpec
{
    /// <summary>Its inner radius; null for the part's default.</summary>
    public double? RadiusMm { get; set; }

    /// <summary>The rip gap where it turns a corner between edges.</summary>
    public double GapMm { get; set; } = 0.5;

    public BendPosition Position { get; set; } = BendPosition.MaterialInside;

    /// <summary>Its own allowance; null for the part's default.</summary>
    public BendAllowance? Allowance { get; set; }
}

/// <summary>
/// A hem as ISwSheetMetal.Hem makes it. The dimensions a closed or open hem does not take are 0; a teardrop or rolled hem
/// carries SOLIDWORKS' own defaults for them instead (length and gap 10 mm, miter gap 1 mm), because InsertSheetMetalHem2
/// makes no teardrop, and a rolled hem with no faces, when they are 0 (6.B live run, read back from hems made by hand).
/// </summary>
public sealed class HemSpec
{
    public HemKind Kind { get; set; } = HemKind.Closed;

    /// <summary>SOLIDWORKS' inside position (material_inside) rather than outside (bend_outside).</summary>
    public bool Inside { get; set; }

    public bool Reverse { get; set; }
    public double LengthMm { get; set; }
    public double GapMm { get; set; }
    public double AngleDeg { get; set; }
    public double RadiusMm { get; set; }

    /// <summary>The gap left where the hem turns a corner between edges.</summary>
    public double MiterGapMm { get; set; }

    /// <summary>Its own allowance; null for the part's default.</summary>
    public BendAllowance? Allowance { get; set; }
}

/// <summary>A sheet-metal part's defaults, read from its SheetMetal feature.</summary>
public sealed class SheetDefaults
{
    public double ThicknessMm { get; set; }
    public double BendRadiusMm { get; set; }

    /// <summary>The default allowance; all its fields null when SOLIDWORKS uses a bend or gauge table.</summary>
    public BendAllowance Allowance { get; set; } = new BendAllowance();

    /// <summary>The automatic relief, or null when there is none or it is one this stage does not name.</summary>
    public ReliefType? Relief { get; set; }

    /// <summary>Whether the part has automatic relief at all (Relief is null both when it has none and when its type is not named).</summary>
    public bool UseAutoRelief { get; set; }

    /// <summary>SOLIDWORKS' own relief type (swSheetMetalReliefTypes_e), to put back a type this stage does not name.</summary>
    public int ReliefTypeCode { get; set; }

    public double ReliefRatio { get; set; }
}

/// <summary>Defaults to write: each field null to leave it as it is.</summary>
public sealed class SheetDefaultsEdit
{
    public double? ThicknessMm { get; set; }
    public double? BendRadiusMm { get; set; }
    public BendAllowance? Allowance { get; set; }

    /// <summary>Turns automatic relief on with this type.</summary>
    public ReliefType? Relief { get; set; }

    /// <summary>
    /// A rollback's relief: false turns automatic relief off; true turns it on with <see cref="ReliefTypeCode"/> (a type
    /// this stage does not name). Ignored when <see cref="Relief"/> is given.
    /// </summary>
    public bool? UseAutoRelief { get; set; }

    /// <summary>SOLIDWORKS' relief type (swSheetMetalReliefTypes_e) to put back with UseAutoRelief true.</summary>
    public int? ReliefTypeCode { get; set; }

    public double? ReliefRatio { get; set; }
}

/// <summary>A base flange the K-factor fallback wrote: what it held before, the bends that had missed the default, and what SOLIDWORKS answered.</summary>
public sealed class FlangeKFactorWrite
{
    public FlangeKFactorWrite(string flange, bool overrideBefore, double kFactorBefore, IReadOnlyList<string> missed, bool accepted)
    {
        Flange = flange;
        OverrideBefore = overrideBefore;
        KFactorBefore = kFactorBefore;
        Missed = missed;
        Accepted = accepted;
    }

    public string Flange { get; }

    /// <summary>IBaseFlangeFeatureData.OverrideKFactor before the write.</summary>
    public bool OverrideBefore { get; }

    /// <summary>IBaseFlangeFeatureData.KFactor before the write.</summary>
    public double KFactorBefore { get; }

    /// <summary>Each bend that followed the default and did not take it, with the allowance it read, e.g. "BaseBend1 (K 0.5)".</summary>
    public IReadOnlyList<string> Missed { get; }

    /// <summary>What ModifyDefinition answered (not evidence: the caller reads the bends back).</summary>
    public bool Accepted { get; }
}

/// <summary>A base flange as ISwSheetMetal.BaseFlange makes it.</summary>
public sealed class BaseFlangeSpec
{
    public double ThicknessMm { get; set; }
    public double BendRadiusMm { get; set; }
    public BendAllowance Allowance { get; set; } = new BendAllowance { KFactor = 0.5 };

    /// <summary>Thicken to the other side of the sketch.</summary>
    public bool Reverse { get; set; }

    /// <summary>How far an open profile is extruded, mm; 0 for a closed profile.</summary>
    public double DepthMm { get; set; }

    public ReliefType Relief { get; set; } = ReliefType.Rectangular;
    public double ReliefRatio { get; set; } = 0.5;
}

/// <summary>A bend as SOLIDWORKS reads it back.</summary>
public sealed class BendRecord
{
    public BendRecord(object feature, string name, string owner, double angleDeg, double radiusMm, BendAllowance allowance, bool defaultRadius, bool defaultAllowance)
    {
        Feature = feature;
        Name = name;
        Owner = owner;
        AngleDeg = angleDeg;
        RadiusMm = radiusMm;
        Allowance = allowance;
        DefaultRadius = defaultRadius;
        DefaultAllowance = defaultAllowance;
    }

    /// <summary>SOLIDWORKS' sub-feature, for references and its faces.</summary>
    public object Feature { get; }
    public string Name { get; }

    /// <summary>The feature it belongs to.</summary>
    public string Owner { get; }
    public double AngleDeg { get; }

    /// <summary>Its inner radius.</summary>
    public double RadiusMm { get; }
    public BendAllowance Allowance { get; }
    public bool DefaultRadius { get; }
    public bool DefaultAllowance { get; }
}
