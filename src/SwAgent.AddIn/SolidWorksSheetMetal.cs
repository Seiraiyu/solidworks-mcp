using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Sheet metal through IFeatureManager, ISheetMetalFeatureData, IBaseFlangeFeatureData and IOneBendFeatureData (design
/// 454). UI thread only; millimetres and degrees become metres and radians here. Every path follows what CADapter
/// measured on 2026 SP4 (design 454, What the evaluation measured) and is proven again by the sheet-metal live suite, not
/// by unit tests: the 16-argument InsertSheetMetalBaseFlange (the 19-argument version builds nothing), the default radius
/// written with SetOverrideDefaultParameter, cut-list values read with Get2 (Get5 resolves nothing).
/// </summary>
internal sealed partial class SolidWorksDocument : ISwSheetMetal
{
    public ISwSheetMetal SheetMetal => this;

    IFeature? FirstOfType(string type) => TopFeatures().FirstOrDefault(f => f.GetTypeName2() == type);

    public SheetDefaults? Defaults()
    {
        if (!(FirstOfType("SheetMetal")?.GetDefinition() is ISheetMetalFeatureData d)) return null;
        return new SheetDefaults
        {
            ThicknessMm = d.Thickness * Mm,
            BendRadiusMm = d.BendRadius * Mm,
            Allowance = Read(d.GetCustomBendAllowance()),
            Relief = d.UseAutoRelief ? Relief(d.AutoReliefType) : null,
            ReliefRatio = d.ReliefRatio,
        };
    }

    static ReliefType? Relief(int type) => type switch
    {
        (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular => ReliefType.Rectangular,
        (int)swSheetMetalReliefTypes_e.swSheetMetalReliefObround => ReliefType.Obround,
        (int)swSheetMetalReliefTypes_e.swSheetMetalReliefTear => ReliefType.Tear,
        _ => null,
    };

    static int Relief(ReliefType type) => type switch
    {
        ReliefType.Obround => (int)swSheetMetalReliefTypes_e.swSheetMetalReliefObround,
        ReliefType.Tear => (int)swSheetMetalReliefTypes_e.swSheetMetalReliefTear,
        _ => (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
    };

    /// <summary>A custom bend allowance in mm: K-factor, direct allowance or deduction; nothing for a table.</summary>
    static BendAllowance Read(CustomBendAllowance? cba)
    {
        if (cba == null) return new BendAllowance();
        return cba.Type switch
        {
            (int)swBendAllowanceTypes_e.swBendAllowanceKFactor => new BendAllowance { KFactor = cba.KFactor },
            (int)swBendAllowanceTypes_e.swBendAllowanceDirect => new BendAllowance { AllowanceMm = cba.BendAllowance * Mm },
            (int)swBendAllowanceTypes_e.swBendAllowanceDeduction => new BendAllowance { DeductionMm = cba.BendDeduction * Mm },
            _ => new BendAllowance(),
        };
    }

    /// <summary>Writes <paramref name="a"/> into <paramref name="cba"/> (metres).</summary>
    static CustomBendAllowance Write(CustomBendAllowance cba, BendAllowance a)
    {
        if (a.KFactor != null)
        {
            cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
            cba.KFactor = a.KFactor.Value;
        }
        else if (a.AllowanceMm != null)
        {
            cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceDirect;
            cba.BendAllowance = a.AllowanceMm.Value / Mm;
        }
        else if (a.DeductionMm != null)
        {
            cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceDeduction;
            cba.BendDeduction = a.DeductionMm.Value / Mm;
        }
        return cba;
    }

    IEnumerable<IBody2> SheetBodies() => SolidBodies().OfType<IBody2>().Where(b => b.IsSheetMetal());

    public int SheetBodyCount() => SheetBodies().Count();

    public IReadOnlyList<object> SheetFaces() =>
        SheetBodies().FirstOrDefault()?.GetFaces() is object[] faces ? faces.Where(f => f != null).ToList() : new List<object>();

    public IReadOnlyList<BendRecord> Bends()
    {
        var bends = new List<BendRecord>();
        foreach (var top in TopFeatures())
        {
            if (top.GetTypeName2() == "FlatPattern") continue;
            for (var sub = top.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
            {
                if (!(sub.GetDefinition() is IOneBendFeatureData d)) continue;
                bends.Add(new BendRecord(sub, sub.Name, top.Name, d.BendAngle / Rad, d.BendRadius * Mm, Read(d.GetCustomBendAllowance()),
                    d.UseDefaultBendRadius, d.UseDefaultBendAllowance));
            }
        }
        return bends;
    }

    /// <summary>
    /// InsertSheetMetalBaseFlange (16 arguments) with the sketch selected (mark 0): blind, one direction, the allowance
    /// as a CustomBendAllowance, the relief automatic with its ratio. A closed profile ignores the depth, which is then
    /// passed as 10 mm as CADapter did.
    /// </summary>
    public object? BaseFlange(object sketch, BaseFlangeSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(sketch, append: false, mark: 0);
            var fm = Doc.FeatureManager;
            var cba = Write(fm.CreateCustomBendAllowance(), s.Allowance);
            const int blind = (int)swEndConditions_e.swEndCondBlind;
            double depth = s.DepthMm > 0 ? s.DepthMm / Mm : 0.01;
            return fm.InsertSheetMetalBaseFlange(s.ThicknessMm / Mm, s.Reverse, s.BendRadiusMm / Mm, depth, 0, false, blind, blind, 0,
                cba, true, Relief(s.Relief), 0, 0, s.ReliefRatio, true);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    public void SetDefaults(SheetDefaultsEdit e)
    {
        if (e.ThicknessMm != null && FirstOfType("SMBaseFlange") is IFeature flange && flange.GetDefinition() is IBaseFlangeFeatureData bf)
        {
            bf.Thickness = e.ThicknessMm.Value / Mm;
            if (bf.UseGaugeTable) bf.OverrideThickness = true;
            flange.ModifyDefinition(bf, Doc, null);
        }
        if (e.BendRadiusMm == null && e.Allowance == null && e.Relief == null && e.ReliefRatio == null) return;
        var feature = FirstOfType("SheetMetal") ?? throw new SwAgentException(ErrorCode.ApiError, "the part has no SheetMetal feature to change");
        var d = (ISheetMetalFeatureData)feature.GetDefinition();
        d.SetOverrideDefaultParameter(true);
        if (e.BendRadiusMm != null) d.BendRadius = e.BendRadiusMm.Value / Mm;
        if (e.Allowance != null) d.SetCustomBendAllowance(Write(d.GetCustomBendAllowance(), e.Allowance));
        if (e.Relief != null)
        {
            d.UseAutoRelief = true;
            d.AutoReliefType = Relief(e.Relief.Value);
        }
        if (e.ReliefRatio != null) d.ReliefRatio = e.ReliefRatio.Value;
        feature.ModifyDefinition(d, Doc, null);
    }

    public IReadOnlyList<KeyValuePair<string, string>> CutList()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var top in TopFeatures().Where(f => f.GetTypeName2() == "SolidBodyFolder"))
        {
            (top.GetSpecificFeature2() as IBodyFolder)?.UpdateCutList();
            for (var sub = top.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
            {
                if (sub.GetTypeName2() != "CutListFolder") continue;
                var bodies = (sub.GetSpecificFeature2() as IBodyFolder)?.GetBodies() as object[] ?? new object[0];
                if (!bodies.OfType<IBody2>().Any(b => b.IsSheetMetal())) continue;
                var cpm = sub.CustomPropertyManager;
                foreach (var name in cpm.GetNames() as object[] ?? new object[0])
                {
                    cpm.Get2((string)name, out string _, out string resolved);
                    list.Add(new KeyValuePair<string, string>((string)name, resolved ?? ""));
                }
                return list;
            }
        }
        return list;
    }

    public double MmPerUnit() => Doc.LengthUnit switch
    {
        (int)swLengthUnit_e.swCM => 10,
        (int)swLengthUnit_e.swMETER => 1000,
        (int)swLengthUnit_e.swINCHES => 25.4,
        (int)swLengthUnit_e.swFEET => 304.8,
        (int)swLengthUnit_e.swFEETINCHES => 25.4,
        (int)swLengthUnit_e.swANGSTROM => 1e-7,
        (int)swLengthUnit_e.swNANOMETER => 1e-6,
        (int)swLengthUnit_e.swMICRON => 1e-3,
        (int)swLengthUnit_e.swMIL => 0.0254,
        (int)swLengthUnit_e.swUIN => 2.54e-5,
        _ => 1,
    };
}
