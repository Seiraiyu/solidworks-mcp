using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// What an export.part request means in SOLIDWORKS' export preferences (design 371, Export: STEP schema, STL
/// resolution, units always mm), and the file extensions each format is written with. The options are checked here,
/// each failing invalid_argument naming its field. The only place mm and degrees become metres and radians for export.
/// </summary>
public static class ExportSettings
{
    public static string[] Extensions(ExportFormat format) => format switch
    {
        ExportFormat.Step => new[] { ".step", ".stp" },
        ExportFormat.Parasolid => new[] { ".x_t" },
        ExportFormat.Stl => new[] { ".stl" },
        _ => new[] { ".3mf" },
    };

    /// <summary>The STEP schema a request asks for: ap214 unless options say otherwise.</summary>
    public static StepSchema SchemaOf(ExportPartRequest request) => request.Options?.StepSchema ?? StepSchema.Ap214;

    /// <summary>The preferences to set for the call, in order; invalid_argument for an option the format does not take or a custom mesh without its values.</summary>
    public static IReadOnlyList<PreferenceSetting> For(ExportFormat format, ExportOptions? options)
    {
        var o = options ?? new ExportOptions();
        bool mesh = format == ExportFormat.Stl || format == ExportFormat.ThreeMf;
        string name = WireNames.ForEnum(format);
        if (o.StepSchema != null && format != ExportFormat.Step) throw Invalid("options.step_schema", $"step_schema is for step, not {name}");
        if (!mesh)
        {
            if (o.Resolution != null) throw Invalid("options.resolution", $"resolution is for stl and threemf, not {name}");
            if (o.DeviationMm != null) throw Invalid("options.deviation_mm", $"deviation_mm is for stl and threemf, not {name}");
            if (o.AngleDeg != null) throw Invalid("options.angle_deg", $"angle_deg is for stl and threemf, not {name}");
        }
        var list = new List<PreferenceSetting>();
        if (format == ExportFormat.Step)
        {
            list.Add(Integer(nameof(swUserPreferenceIntegerValue_e.swStepAP), (int)swUserPreferenceIntegerValue_e.swStepAP, (o.StepSchema ?? StepSchema.Ap214) == StepSchema.Ap242 ? 242 : 214));
            return list;
        }
        if (!mesh) return list;

        bool custom = o.Resolution == MeshResolution.Custom || o.Resolution == null && (o.DeviationMm != null || o.AngleDeg != null);
        if (!custom && o.DeviationMm != null) throw Invalid("options.deviation_mm", "deviation_mm needs resolution custom");
        if (!custom && o.AngleDeg != null) throw Invalid("options.angle_deg", "angle_deg needs resolution custom");
        if (custom)
        {
            if (o.DeviationMm == null || !(o.DeviationMm > 0)) throw Invalid("options.deviation_mm", "a custom resolution needs deviation_mm, more than 0 mm");
            if (o.AngleDeg == null || !(o.AngleDeg > 0 && o.AngleDeg < 90)) throw Invalid("options.angle_deg", "a custom resolution needs angle_deg, more than 0 and less than 90 degrees");
        }
        var quality = custom ? swSTLQuality_e.swSTLQuality_Custom
            : o.Resolution == MeshResolution.Coarse ? swSTLQuality_e.swSTLQuality_Coarse
            : swSTLQuality_e.swSTLQuality_Fine;
        list.Add(Integer(nameof(swUserPreferenceIntegerValue_e.swExportStlUnits), (int)swUserPreferenceIntegerValue_e.swExportStlUnits, (int)swLengthUnit_e.swMM));
        list.Add(Integer(nameof(swUserPreferenceIntegerValue_e.swSTLQuality), (int)swUserPreferenceIntegerValue_e.swSTLQuality, (int)quality));
        if (custom)
        {
            list.Add(new PreferenceSetting(nameof(swUserPreferenceDoubleValue_e.swSTLDeviation), PreferenceKind.Double, (int)swUserPreferenceDoubleValue_e.swSTLDeviation, o.DeviationMm!.Value / 1000));
            list.Add(new PreferenceSetting(nameof(swUserPreferenceDoubleValue_e.swSTLAngleTolerance), PreferenceKind.Double, (int)swUserPreferenceDoubleValue_e.swSTLAngleTolerance, o.AngleDeg!.Value * Math.PI / 180));
        }
        if (format == ExportFormat.Stl)
        {
            list.Add(Toggle(nameof(swUserPreferenceToggle_e.swSTLBinaryFormat), (int)swUserPreferenceToggle_e.swSTLBinaryFormat, true));
            list.Add(Toggle(nameof(swUserPreferenceToggle_e.swSTLDontTranslateToPositive), (int)swUserPreferenceToggle_e.swSTLDontTranslateToPositive, true));
            list.Add(Toggle(nameof(swUserPreferenceToggle_e.swSTLShowInfoOnSave), (int)swUserPreferenceToggle_e.swSTLShowInfoOnSave, false));
            list.Add(Toggle(nameof(swUserPreferenceToggle_e.swSTLPreview), (int)swUserPreferenceToggle_e.swSTLPreview, false));
        }
        else
        {
            list.Add(Toggle(nameof(swUserPreferenceToggle_e.sw3MFShowInfoOnSave), (int)swUserPreferenceToggle_e.sw3MFShowInfoOnSave, false));
        }
        return list;
    }

    static PreferenceSetting Integer(string name, int id, int value) => new PreferenceSetting(name, PreferenceKind.Integer, id, value);

    static PreferenceSetting Toggle(string name, int id, bool value) => new PreferenceSetting(name, PreferenceKind.Toggle, id, value ? 1 : 0);

    static SwAgentException Invalid(string field, string why) =>
        new SwAgentException(ErrorCode.InvalidArgument, $"{field}: {why}", new JsonObject { ["field"] = field });
}

/// <summary>SOLIDWORKS' user preferences of every kind, through the ISldWorks the add-in was given. UI thread only.</summary>
internal sealed class SolidWorksTypedPreferenceStore : ITypedPreferenceStore
{
    readonly ISldWorks _sw;

    public SolidWorksTypedPreferenceStore(ISldWorks sw)
    {
        _sw = sw;
    }

    public double Get(PreferenceKind kind, int id) => kind switch
    {
        PreferenceKind.Toggle => _sw.GetUserPreferenceToggle(id) ? 1 : 0,
        PreferenceKind.Integer => _sw.GetUserPreferenceIntegerValue(id),
        _ => _sw.GetUserPreferenceDoubleValue(id),
    };

    public void Set(PreferenceKind kind, int id, double value)
    {
        switch (kind)
        {
            case PreferenceKind.Toggle:
                _sw.SetUserPreferenceToggle(id, value != 0);
                break;
            case PreferenceKind.Integer:
                _sw.SetUserPreferenceIntegerValue(id, (int)Math.Round(value));
                break;
            default:
                _sw.SetUserPreferenceDoubleValue(id, value);
                break;
        }
    }
}
