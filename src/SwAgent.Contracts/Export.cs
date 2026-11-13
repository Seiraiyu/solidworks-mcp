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

    [Description("AP242 (managed model-based 3D engineering), published by SOLIDWORKS MBD (PublishSTEP242File, then PublishSTEP242File2); operation_failed listing every attempt and SOLIDWORKS' answer where it will not publish one, as on a seat without SOLIDWORKS MBD.")]
    Ap242,
}

[Description("How finely an STL or 3MF export meshes the part (SOLIDWORKS' STL/3MF/AMF export options).")]
public enum MeshResolution
{
    [Description("SOLIDWORKS' coarse mesh.")]
    Coarse,

    [Description("SOLIDWORKS' fine mesh.")]
    Fine,
}

[Description("A format export.flat_pattern writes.")]
public enum FlatPatternFormat
{
    [Description("DXF (.dxf), ASCII, checked against the flat size.")]
    Dxf,

    [Description("DWG (.dwg), checked by its header and size.")]
    Dwg,
}

[Operation("export.part", typeof(ExportPartResult))]
[Description("Writes a copy of a part as STEP, Parasolid (.x_t), binary STL or 3MF, in mm; the part keeps its own path and unsaved changes. A part that is not the active document is made active for the export, and the document that was active is made active again afterwards. The export options it needs (STEP schema, STL units and format, and the mesh resolution when one is asked) are set for the call and put back afterwards; without a resolution, STL and 3MF use the user's own mesh settings untouched. A resolution is refused when the user's is custom, which SOLIDWORKS cannot put back. Checked on disk: the file was written by this call, is not empty, and has its format's header (ISO-10303-21; and the schema asked, the Parasolid transmit header, a binary STL whose size matches its triangle count, a 3MF zip holding 3D/3dmodel.model).")]
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

    [Description("STL and 3MF only: coarse or fine, set for the call. Absent: the user's own mesh settings, untouched. Refused (invalid_argument) when the user's resolution is custom, which SOLIDWORKS does not take back through its API.")]
    public MeshResolution? Resolution { get; set; }
}

[Operation("export.flat_pattern", typeof(ExportFlatPatternResult))]
[Description("Writes a sheet-metal part's flat pattern as DXF or DWG, the file a cutting machine takes, in the part's units. The part must be saved to disk: a part never saved is refused before SOLIDWORKS is called (save it with sw_document_save_as first), because SOLIDWORKS then opens a Save As box that waits for the user. A part with unsaved changes is exported as it is now. The part need not be flattened: it keeps its state (folded or flattened) and its path, and the part that was active is made active again. On SOLIDWORKS 2026 SP4 every export marks a saved part as having unsaved changes, and the API cannot clear that: then marked_dirty is true, and the part is not saved behind you (save it with sw_document_save to clear it). Bend lines are drawn unless bend_lines is false; the part's visible sketches only when sketches is true. Checked on disk: the file was written by this call and is not empty; a DXF parses, the extents of its entities match the flat size from the cut list (sw_sheetmetal_read's flat) within 0.01 mm beyond the cut list's own rounding, turned or not (the header's extents are reported, not compared), and it holds a bend line for each bend when bend lines were asked for, and none when they were not (with sketches drawn that is reported, not checked: a sketch's lines look the same); a DWG starts with its AC10xx version and is at least 1 KB (its extents are not read). Otherwise verification_failed, naming the check; the file is left where it was written. A SOLIDWORKS call that writes nothing is followed by the next (with sketches or unsaved changes, ExportToDWG2 alone, since ExportFlatPatternView writes the saved base for a part with unsaved changes; with bend lines, ExportToDWG2 first, since ExportFlatPatternView draws none; otherwise ExportFlatPatternView, then ExportToDWG2), and operation_failed lists every attempt when none writes.")]
public sealed class ExportFlatPatternRequest
{
    [Description("The sheet-metal part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("Full path of the file to write, in an existing folder, ending .dxf or .dwg as format says.")]
    public string Path { get; set; } = "";

    [Description("dxf or dwg.")]
    public FlatPatternFormat Format { get; set; }

    [Description("Draw the bend lines. Default true.")]
    public bool? BendLines { get; set; }

    [Description("Draw the part's visible sketches too. Default false.")]
    public bool? Sketches { get; set; }

    [Description("Replace the file if it exists. Default false.")]
    public bool? Overwrite { get; set; }
}

[Description("The flat pattern export.flat_pattern wrote, as read back from disk.")]
public sealed class ExportFlatPatternResult
{
    [Required]
    [Description("The part, as it is after the export: same path, same unsaved-changes flag.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The file written.")]
    public string Path { get; set; } = "";

    [Description("Its format.")]
    public FlatPatternFormat Format { get; set; }

    [Description("Its size in bytes.")]
    public long Bytes { get; set; }

    [Required]
    [Description("What it starts with: DXF, or the DWG's version, such as AC1032.")]
    public string Header { get; set; } = "";

    [Required]
    [Description("The SOLIDWORKS call that wrote it: ExportFlatPatternView or ExportToDWG2.")]
    public string Method { get; set; } = "";

    [Required]
    [Description("Each call tried, in order, with what SOLIDWORKS answered and whether it wrote the file.")]
    public List<string> Attempts { get; set; } = new List<string>();

    [Description("folded or flattened: the part's state, the same before and after.")]
    public SheetState State { get; set; }

    [Description("The flat size from the cut list the drawing was checked against; absent when the cut list does not give it (a DWG is still written).")]
    public FlatSize? Flat { get; set; }

    [Description("A DXF's drawing extents; absent for a DWG.")]
    public DrawingExtents? Extents { get; set; }

    [Description("A DXF's bend lines: lines standing on their own, outside the outline's chain, whatever their layer (a visible sketch's free lines count too); absent for a DWG.")]
    public int? BendLines { get; set; }

    [Description("True when the SOLIDWORKS call marked a part that had no unsaved changes as having them: every export does on SOLIDWORKS 2026 SP4, and the API cannot clear the flag. The part was not saved to clear it; its path, state and geometry are unchanged.")]
    public bool MarkedDirty { get; set; }

    [Required]
    [Description("A DXF's layers, each as name = entities on it; empty for a DWG.")]
    public List<string> Layers { get; set; } = new List<string>();

    [Required]
    [Description("Each check made on the file and the part, and what it found.")]
    public List<string> Checks { get; set; } = new List<string>();
}

[Description("A drawing's extents: the rectangle its entities fill, mm.")]
public sealed class DrawingExtents
{
    [Description("Its smallest x, mm.")]
    public double MinXMm { get; set; }

    [Description("Its smallest y, mm.")]
    public double MinYMm { get; set; }

    [Description("Its largest x, mm.")]
    public double MaxXMm { get; set; }

    [Description("Its largest y, mm.")]
    public double MaxYMm { get; set; }

    [Description("Its longer side, mm.")]
    public double LengthMm { get; set; }

    [Description("Its shorter side, mm.")]
    public double WidthMm { get; set; }

    [Required]
    [Description("Where they were read: its entities (the header's $EXTMIN/$EXTMAX is reported in checks, not compared).")]
    public string From { get; set; } = "";
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
