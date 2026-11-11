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

    /// <summary>The sheet-metal body's cut-list properties, each name with its resolved value, after updating the cut list; empty when there is none.</summary>
    IReadOnlyList<KeyValuePair<string, string>> CutList();

    /// <summary>How many millimetres one of the document's length units is (the unit cut-list values are written in).</summary>
    double MmPerUnit();
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

    public double ReliefRatio { get; set; }
}

/// <summary>Defaults to write: each field null to leave it as it is.</summary>
public sealed class SheetDefaultsEdit
{
    public double? ThicknessMm { get; set; }
    public double? BendRadiusMm { get; set; }
    public BendAllowance? Allowance { get; set; }
    public ReliefType? Relief { get; set; }
    public double? ReliefRatio { get; set; }
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
