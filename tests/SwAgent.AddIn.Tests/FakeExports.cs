using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using SwAgent.AddIn.Core;

namespace SwAgent.AddIn.Tests;

/// <summary>SOLIDWORKS' user preferences in memory, as export.part sets and reads them back.</summary>
sealed class FakePreferences : ITypedPreferenceStore
{
    public readonly Dictionary<(PreferenceKind, int), double> Values = new Dictionary<(PreferenceKind, int), double>();

    /// <summary>Preferences SOLIDWORKS does not keep when set.</summary>
    public readonly HashSet<int> Ignored = new HashSet<int>();

    public double Get(PreferenceKind kind, int id) => Values.TryGetValue((kind, id), out var v) ? v : 0;

    public void Set(PreferenceKind kind, int id, double value)
    {
        if (!Ignored.Contains(id)) Values[(kind, id)] = value;
    }
}

/// <summary>Exports as SOLIDWORKS writes them: a file of the extension's format, shaped by the preferences at the time of the call.</summary>
sealed partial class FakeDocument
{
    /// <summary>The preferences an export reads; set by the test.</summary>
    public FakePreferences? Prefs { get; set; }

    /// <summary>Each export's path and the preferences as they were during it.</summary>
    public List<(string Path, Dictionary<(PreferenceKind, int), double> Prefs)> Exports { get; } = new List<(string, Dictionary<(PreferenceKind, int), double>)>();

    /// <summary>The next export reports SOLIDWORKS' generic save error and writes nothing.</summary>
    public bool ExportFails { get; set; }

    /// <summary>An export reports success but writes nothing.</summary>
    public bool ExportWritesNothing { get; set; }

    /// <summary>An export writes these bytes instead of the format's.</summary>
    public byte[]? ExportWrites { get; set; }

    /// <summary>An export makes the document that file (SaveAs without the copy option).</summary>
    public bool ExportMovesDocument { get; set; }

    /// <summary>Triangles a binary STL holds: coarse 12, fine 44, custom 100, as the fake meshes the block.</summary>
    public static int Triangles(double quality) => quality == 1 ? 12 : quality == 3 ? 100 : 44;

    /// <summary>Each AP242 publish's path and the preferences as they were during it.</summary>
    public List<(string Path, Dictionary<(PreferenceKind, int), double> Prefs)> Published { get; } = new List<(string, Dictionary<(PreferenceKind, int), double>)>();

    /// <summary>The swStep242Error_e the next publish answers; 0 writes the file.</summary>
    public int PublishFails { get; set; }

    public int PublishStep242(string path)
    {
        var prefs = Prefs ?? new FakePreferences();
        Published.Add((path, new Dictionary<(PreferenceKind, int), double>(prefs.Values)));
        if (PublishFails != 0) return PublishFails;
        File.WriteAllBytes(path, ExportWrites ?? Encoding.ASCII.GetBytes(
            "ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION (( 'STEP AP242' ), '1' );\r\nFILE_SCHEMA (( 'AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }' ));\r\nENDSEC;\r\nDATA;\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n"));
        return 0;
    }

    public SaveOutcome Export(string path)
    {
        var prefs = Prefs ?? new FakePreferences();
        Exports.Add((path, new Dictionary<(PreferenceKind, int), double>(prefs.Values)));
        if (ExportFails) return new SaveOutcome(false, 1, 0);
        if (ExportMovesDocument)
        {
            Document.Path = path;
            Document.Title = Path.GetFileName(path);
        }
        if (ExportWritesNothing) return new SaveOutcome(true, 0, 0);
        File.WriteAllBytes(path, ExportWrites ?? Bytes(path, prefs));
        return new SaveOutcome(true, 0, 0);
    }

    static byte[] Bytes(string path, FakePreferences prefs)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".step":
            case ".stp":
                string schema = prefs.Get(PreferenceKind.Integer, 75) == 242
                    ? "AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }"
                    : "AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }";
                return Encoding.ASCII.GetBytes($"ISO-10303-21;\r\nHEADER;\r\nFILE_SCHEMA (( '{schema}' ));\r\nENDSEC;\r\nDATA;\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n");
            case ".x_t":
                return Encoding.ASCII.GetBytes(ExportFileCheck.ParasolidHeader + "**************************\n**PARASOLID !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~0123456789**************************\n");
            case ".stl":
                if (prefs.Get(PreferenceKind.Toggle, 69) == 0) return Encoding.ASCII.GetBytes("solid block\n facet normal 0 0 1\n" + new string(' ', 200) + "endsolid block\n");
                int triangles = Triangles(prefs.Get(PreferenceKind.Integer, 78));
                var stl = new byte[84 + 50 * triangles];
                Encoding.ASCII.GetBytes("solid block").CopyTo(stl, 0);
                BitConverter.GetBytes((uint)triangles).CopyTo(stl, 80);
                return stl;
            default:
                using (var memory = new MemoryStream())
                {
                    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var entry in new[] { "[Content_Types].xml", "_rels/.rels", "3D/3dmodel.model" })
                        {
                            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
                            w.Write("<model unit=\"millimeter\"/>");
                        }
                    }
                    return memory.ToArray();
                }
        }
    }
}
