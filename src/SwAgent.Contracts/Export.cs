using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A format export.part writes.")]
public enum ExportFormat
{
    [Description("STEP (.step or .stp), the schema options.step_schema names.")]
    Step,

    [Description("Parasolid text transmit file (.x_t).")]
    Parasolid,

    [Description("Binary STL (.stl), in mm.")]
    Stl,

    [Description("3MF (.3mf), in mm.")]
    [WireName("threemf")]
    ThreeMf,
}

[Description("The STEP application protocol a STEP export uses.")]
public enum StepSchema
{
    [Description("AP214 (automotive design), SOLIDWORKS' default.")]
    Ap214,

    [Description("AP242 (managed model-based 3D engineering).")]
    Ap242,
}

[Description("How finely an STL or 3MF export meshes the part (SOLIDWORKS' STL/3MF/AMF export options).")]
public enum MeshResolution
{
    [Description("SOLIDWORKS' coarse mesh.")]
    Coarse,

    [Description("SOLIDWORKS' fine mesh, the default.")]
    Fine,

    [Description("The deviation and angle given in options.")]
    Custom,
}

[Operation("export.part", typeof(ExportPartResult))]
[Description("Writes a copy of a part as STEP, Parasolid (.x_t), binary STL or 3MF, in mm; the part keeps its own path and unsaved changes. A part that is not the active document is made active for the export, and the document that was active is made active again afterwards. The export options it needs (STEP schema, mesh resolution, STL units and format) are set for the call and put back afterwards. Checked on disk: the file was written by this call, is not empty, and has its format's header (ISO-10303-21; and the schema asked, the Parasolid transmit header, a binary STL whose size matches its triangle count, a 3MF zip holding 3D/3dmodel.model).")]
public sealed class ExportPartRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("Full path of the file to write, in an existing folder, with the format's extension: .step or .stp, .x_t, .stl, .3mf.")]
    public string Path { get; set; } = "";

    [Description("The format to write.")]
    public ExportFormat Format { get; set; }

    [Description("Replace the file if it exists. Default false.")]
    public bool? Overwrite { get; set; }

    [Description("The format's options; its defaults when absent.")]
    public ExportOptions? Options { get; set; }
}

[Description("Export options, each for the formats it names. STL and 3MF are always written in mm.")]
public sealed class ExportOptions
{
    [Description("STEP only: the application protocol. Default ap214.")]
    public StepSchema? StepSchema { get; set; }

    [Description("STL and 3MF only: coarse, fine (the default) or custom, which takes deviation_mm and angle_deg.")]
    public MeshResolution? Resolution { get; set; }

    [Description("STL and 3MF, custom resolution: the largest distance between the mesh and the surface, mm, more than 0.")]
    public double? DeviationMm { get; set; }

    [Description("STL and 3MF, custom resolution: the largest angle between neighbouring triangles, degrees, more than 0 and less than 90.")]
    public double? AngleDeg { get; set; }
}

[Description("The file export.part wrote, as read back from disk.")]
public sealed class ExportPartResult
{
    [Required]
    [Description("The part exported, as it is after the export: same path, same unsaved-changes flag.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The file written.")]
    public string Path { get; set; } = "";

    [Description("Its format.")]
    public ExportFormat Format { get; set; }

    [Description("Its size in bytes.")]
    public long Bytes { get; set; }

    [Required]
    [Description("The header found: ISO-10303-21;, the Parasolid transmit header, binary STL, or zip.")]
    public string Header { get; set; } = "";

    [Description("STL: the triangles it holds.")]
    public int? Triangles { get; set; }

    [Description("STEP: its FILE_SCHEMA, for example AUTOMOTIVE_DESIGN { 1 0 10303 214 3 1 1 }.")]
    public string? StepSchema { get; set; }

    [Required]
    [Description("The SOLIDWORKS export preferences set for the call and put back afterwards, each as name = value.")]
    public List<string> Preferences { get; set; } = new List<string>();

    [Required]
    [Description("What SOLIDWORKS warned about while exporting (swFileSaveWarning_e names).")]
    public List<string> Warnings { get; set; } = new List<string>();

    [Required]
    [Description("Each check made on the file and the part, and what it found.")]
    public List<string> Checks { get; set; } = new List<string>();
}
