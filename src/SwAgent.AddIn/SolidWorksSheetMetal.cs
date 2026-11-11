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
            UseAutoRelief = d.UseAutoRelief,
            ReliefTypeCode = d.AutoReliefType,
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
        bool relief = e.Relief != null || e.UseAutoRelief != null || e.ReliefRatio != null;
        if (e.BendRadiusMm == null && e.Allowance == null && !relief) return;
        var feature = FirstOfType("SheetMetal") ?? throw new SwAgentException(ErrorCode.ApiError, "the part has no SheetMetal feature to change");
        var d = (ISheetMetalFeatureData)feature.GetDefinition();
        // Each kind of default has its own override switch: SetOverrideDefaultParameter covers the bend parameters (the
        // radius) only, and without the allowance's and the relief's own switch ModifyDefinition keeps their old values
        // and still says true (6.A live run: K 0.1 read back as 0.5).
        d.SetOverrideDefaultParameter(true);
        if (e.Allowance != null) d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_BendAllowance, true);
        if (relief) d.SetOverrideDefaultParameter2((int)swSheetMetalOverrideDefaultParameters_e.swSheetMetalOverrideDefaultParameters_AutoRelief, true);
        if (e.BendRadiusMm != null) d.BendRadius = e.BendRadiusMm.Value / Mm;
        if (e.Allowance != null) d.SetCustomBendAllowance(Write(d.GetCustomBendAllowance(), e.Allowance));
        if (e.Relief != null)
        {
            d.UseAutoRelief = true;
            d.AutoReliefType = Relief(e.Relief.Value);
        }
        else if (e.UseAutoRelief == false) d.UseAutoRelief = false;
        else if (e.UseAutoRelief == true)
        {
            d.UseAutoRelief = true;
            if (e.ReliefTypeCode != null) d.AutoReliefType = e.ReliefTypeCode.Value;
        }
        if (e.ReliefRatio != null) d.ReliefRatio = e.ReliefRatio.Value;
        feature.ModifyDefinition(d, Doc, null);
    }

    /// <summary>The features whose bends take a K-factor of their own: base flanges, edge flanges, miter flanges and hems.</summary>
    static readonly string[] FlangeTypes = { "SMBaseFlange", "EdgeFlange", "SMMiteredFlange", "Hem" };

    /// <summary>
    /// A flange's own K-factor: for a base flange its OverrideKFactor and KFactor; for the others, whether it does not
    /// use the default allowance and the K-factor of its own CustomBendAllowance (0.5 when that is not a K-factor).
    /// </summary>
    static (bool Override, double K)? FlangeK(object? definition)
    {
        double K(CustomBendAllowance? cba) => cba != null && cba.Type == (int)swBendAllowanceTypes_e.swBendAllowanceKFactor ? cba.KFactor : 0.5;
        return definition switch
        {
            IBaseFlangeFeatureData b => (b.OverrideKFactor, b.KFactor),
            IEdgeFlangeFeatureData e => (!e.UseDefaultBendAllowance, K(e.GetCustomBendAllowance())),
            IMiterFlangeFeatureData m => (!m.UseDefaultBendAllowance, K(m.GetCustomBendAllowance())),
            IHemFeatureData h => (!h.UseDefaultBendAllowance, K(h.GetCustomBendAllowance())),
            _ => null,
        };
    }

    /// <summary>Writes a flange's own K-factor into its definition (not yet applied): see <see cref="FlangeK"/>.</summary>
    static bool WriteFlangeK(object definition, bool overrideKFactor, double k)
    {
        CustomBendAllowance Kf(CustomBendAllowance cba)
        {
            cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
            cba.KFactor = k;
            return cba;
        }
        switch (definition)
        {
            case IBaseFlangeFeatureData b:
                b.KFactor = k;
                b.OverrideKFactor = overrideKFactor;
                return true;
            case IEdgeFlangeFeatureData e:
                e.UseDefaultBendAllowance = !overrideKFactor;
                e.SetCustomBendAllowance(Kf(e.GetCustomBendAllowance()));
                return true;
            case IMiterFlangeFeatureData m:
                m.UseDefaultBendAllowance = !overrideKFactor;
                m.SetCustomBendAllowance(Kf(m.GetCustomBendAllowance()));
                return true;
            case IHemFeatureData h:
                h.UseDefaultBendAllowance = !overrideKFactor;
                h.SetCustomBendAllowance(Kf(h.GetCustomBendAllowance()));
                return true;
            default:
                return false;
        }
    }

    public IReadOnlyList<FlangeKFactorWrite> KFactorToBaseFlanges(double k, double? oldK)
    {
        var written = new List<FlangeKFactorWrite>();
        foreach (var flange in TopFeatures().Where(f => FlangeTypes.Contains(f.GetTypeName2())).ToList())
        {
            var missed = new List<string>();
            for (var sub = flange.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
            {
                if (!(sub.GetDefinition() is IOneBendFeatureData b)) continue;
                var allowance = Read(b.GetCustomBendAllowance());
                double? bendK = allowance.KFactor;
                bool follows = b.UseDefaultBendAllowance || (oldK != null && bendK != null && Math.Abs(bendK.Value - oldK.Value) <= 1e-6);
                if (follows && (bendK == null || Math.Abs(bendK.Value - k) > 1e-9)) missed.Add($"{sub.Name} ({allowance})");
            }
            object definition = flange.GetDefinition();
            if (missed.Count == 0 || !(FlangeK(definition) is (bool overrideBefore, double kBefore))) continue;
            WriteFlangeK(definition, true, k);
            bool accepted = flange.ModifyDefinition(definition, Doc, null);
            written.Add(new FlangeKFactorWrite(flange.Name, overrideBefore, kBefore, missed, accepted));
        }
        return written;
    }

    public void SetBaseFlangeKFactor(string flange, bool overrideKFactor, double kFactor)
    {
        var feature = TopFeatures().FirstOrDefault(f => f.Name == flange && FlangeTypes.Contains(f.GetTypeName2()))
            ?? throw new SwAgentException(ErrorCode.ApiError, $"the part has no flange {flange} to put back");
        object definition = feature.GetDefinition();
        if (!WriteFlangeK(definition, overrideKFactor, kFactor))
            throw new SwAgentException(ErrorCode.ApiError, $"{flange}'s definition is not a flange's");
        feature.ModifyDefinition(definition, Doc, null);
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
