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
        if (!mesh && o.Resolution != null) throw Invalid("options.resolution", $"resolution is for stl and threemf, not {name}");
        var list = new List<PreferenceSetting>();
        if (format == ExportFormat.Step)
        {
            list.Add(Integer(nameof(swUserPreferenceIntegerValue_e.swStepAP), (int)swUserPreferenceIntegerValue_e.swStepAP, (o.StepSchema ?? StepSchema.Ap214) == StepSchema.Ap242 ? 242 : 214));
            return list;
        }
        if (!mesh) return list;

        list.Add(Integer(nameof(swUserPreferenceIntegerValue_e.swExportStlUnits), (int)swUserPreferenceIntegerValue_e.swExportStlUnits, (int)swLengthUnit_e.swMM));
        // Without a resolution the user's own mesh settings are used and never touched. With one, it is set for the
        // call, unless the user's is custom: SOLIDWORKS does not take swSTLQuality = 3 through its API (3.E live run,
        // round 3), so a custom resolution changed for the call could never be put back.
        if (o.Resolution != null)
        {
            var quality = o.Resolution == MeshResolution.Coarse ? swSTLQuality_e.swSTLQuality_Coarse : swSTLQuality_e.swSTLQuality_Fine;
            list.Add(new PreferenceSetting(nameof(swUserPreferenceIntegerValue_e.swSTLQuality), PreferenceKind.Integer, (int)swUserPreferenceIntegerValue_e.swSTLQuality, (int)quality,
                new[] { (double)(int)swSTLQuality_e.swSTLQuality_Custom }, "options.resolution",
                "the user's STL/3MF resolution is custom, which SOLIDWORKS does not take back through its API; omit options.resolution to export with the user's own mesh settings, or ask the user to choose coarse or fine in Tools > Options > Export"));
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
