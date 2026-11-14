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
    [Description("The part: its title, its full path, or its file name with the extension (e.g. part.SLDPRT, part.SLDDRW), which picks exactly that document. A name several open documents share is refused with the candidates. The active document when absent.")]
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
    [Description("The sheet-metal part: its title, its full path, or its file name with the extension (e.g. part.SLDPRT, part.SLDDRW), which picks exactly that document. A name several open documents share is refused with the candidates. The active document when absent.")]
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

    [Description("True when the SOLIDWORKS call marked a part that had no unsaved changes as having them: every export does on SOLIDWORKS 2026 SP4, and the API cannot clear the flag. The part was not saved to clear it; its path, state and geometry are unchanged (checked: its volume, bend count and cut-list flat size are measured before and after the call).")]
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

[Operation("export.assembly", typeof(ExportAssemblyResult))]
[Description("Writes a copy of an assembly as STEP, Parasolid (.x_t), binary STL or 3MF, in mm; the assembly keeps its own path and unsaved changes, and so do its components. STEP and Parasolid keep the assembly's structure (one occurrence per component); STL and 3MF are one file of the whole assembly. An assembly that is not the active document is made active for the export, and the document that was active is made active again afterwards. The export options it needs (STEP schema, STL units, format and one file for all components, and the mesh resolution when one is asked) are set for the call and put back afterwards; without a resolution, STL and 3MF use the user's own mesh settings untouched. A resolution is refused when the user's is custom. A lightweight component is refused: its geometry is not loaded (component.set suppression resolved loads it). Checked on disk: the file was written by this call, is not empty, and has its format's header (ISO-10303-21; and the schema asked, the Parasolid transmit header, a binary STL whose size matches its triangle count, a 3MF zip holding 3D/3dmodel.model); a STEP file holds one NEXT_ASSEMBLY_USAGE_OCCURRENCE per resolved component, nested ones included. A part is refused (export.part writes parts).")]
public sealed class ExportAssemblyRequest
{
    [Description("The assembly: its title, its full path, or its file name with the extension (e.g. part.SLDPRT, part.SLDDRW), which picks exactly that document. A name several open documents share is refused with the candidates. The active document when absent.")]
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

[Description("The file export.assembly wrote, as read back from disk.")]
public sealed class ExportAssemblyResult
{
    [Required]
    [Description("The assembly exported, as it is after the export: same path, same unsaved-changes flag.")]
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

    [Description("How many resolved components the assembly has, nested ones included: what the export covers.")]
    public int Components { get; set; }

    [Description("STEP: the NEXT_ASSEMBLY_USAGE_OCCURRENCE entities it holds, one per component.")]
    public int? StepOccurrences { get; set; }

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
    [Description("Each check made on the file and the assembly, and what it found.")]
    public List<string> Checks { get; set; } = new List<string>();
}

[Description("A format export.drawing writes.")]
public enum DrawingExportFormat
{
    [Description("PDF (.pdf): one file, a page per sheet, each page its sheet's size.")]
    Pdf,

    [Description("DXF (.dxf), ASCII: one file per sheet, in sheet mm from the sheet's lower-left corner.")]
    Dxf,

    [Description("DWG (.dwg): one file per sheet, checked by its header and size.")]
    Dwg,
}

[Operation("export.drawing", typeof(ExportDrawingResult))]
[Description("Writes a drawing's sheets as PDF, DXF or DWG: every sheet when sheets is absent, else the ones named (by reference r1.sheet.… or name), in the drawing's order. PDF is one file with a page per sheet. DXF and DWG are one file per sheet: the path itself for one sheet, and for several <folder>\\<name>-<sheet>.dxf (or .dwg) beside it, the sheet's name with any character a file name cannot hold made _. Each sheet is shown in turn for DXF and DWG, and the sheet that was shown is shown again afterwards. The drawing must be saved to disk: a drawing never saved is refused before SOLIDWORKS is called (save it with sw_document_save_as first), because SOLIDWORKS then opens a Save As box that waits for the user. The export options it needs are set for the call and put back afterwards: no PDF viewer opened after saving; DXF and DWG of the shown sheet only, at sheet scale (not 1:1 model scale), views as plain entities (not blocks), no mapping file and no hidden-layer warning. Checked on disk: each file was written by this call and is not empty; a PDF starts %PDF-, has one page per sheet exported, and each page is its sheet's size within 0.1 mm (turned or not); a DXF parses, every entity lies on its sheet (within 0.1 mm), and the largest view's drawn extents (sw_drawing_read's extents, sheet mm) are found among its entities within 0.01 mm (at least the view's scale × 0.01 mm); a DWG starts with its AC10xx version and is at least 1 KB (its contents are not read). Otherwise verification_failed, naming the check; the files are left where they were written. The drawing keeps its path, sheets, views, annotations and tables (measured before and after), and every model it shows keeps its unsaved-changes flag. On SOLIDWORKS 2026 SP4 an export may mark a saved drawing as having unsaved changes, and the API cannot clear that: then marked_dirty is true, and the drawing is not saved behind you (save it with sw_document_save to clear it). Waits, writing nothing (busy, user_editing), while the user edits a view's sketch or the sheet format or has a command open. A part or an assembly is refused (export.part, export.assembly and export.flat_pattern write those).")]
public sealed class ExportDrawingRequest
{
    [Description("The drawing: its title, its full path, or its file name with the extension (e.g. part.SLDPRT, part.SLDDRW), which picks exactly that document. A name several open documents share is refused with the candidates. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("Full path of the file to write, in an existing folder, ending .pdf, .dxf or .dwg as format says. For DXF or DWG of several sheets, the name each sheet's file is named after.")]
    public string Path { get; set; } = "";

    [Description("pdf, dxf or dwg.")]
    public DrawingExportFormat Format { get; set; }

    [Description("The sheets to write, each a reference (r1.sheet.…) or a name (Sheet1). Every sheet when absent.")]
    public List<string>? Sheets { get; set; }

    [Description("Replace the files if they exist. Default false.")]
    public bool? Overwrite { get; set; }
}

[Description("A PDF page export.drawing wrote, as read back from the file.")]
public sealed class ExportedPage
{
    [Required]
    [Description("The sheet it shows.")]
    public string Sheet { get; set; } = "";

    [Description("Its width as shown (its media box, turned by its /Rotate), mm.")]
    public double WidthMm { get; set; }

    [Description("Its height as shown, mm.")]
    public double HeightMm { get; set; }
}

[Description("A file export.drawing wrote, as read back from disk.")]
public sealed class DrawingExportFile
{
    [Required]
    [Description("The file written.")]
    public string Path { get; set; } = "";

    [Required]
    [Description("The sheets it holds, in order.")]
    public List<string> Sheets { get; set; } = new List<string>();

    [Description("Its size in bytes.")]
    public long Bytes { get; set; }

    [Required]
    [Description("What it starts with: %PDF- and its version, DXF, or the DWG's version (AC1032).")]
    public string Header { get; set; } = "";

    [Required]
    [Description("A PDF's pages, in order; empty for DXF and DWG.")]
    public List<ExportedPage> Pages { get; set; } = new List<ExportedPage>();

    [Description("A DXF's extents: the rectangle its entities fill, sheet mm; absent for PDF and DWG.")]
    public DrawingExtents? Extents { get; set; }

    [Description("A DXF's largest view, whose drawn extents were found among its entities; absent when its sheet has no view, and for PDF and DWG.")]
    public string? View { get; set; }

    [Description("The extents that view's entities fill in the file, sheet mm.")]
    public SheetBox? ViewExtents { get; set; }
}

[Description("The files export.drawing wrote, as read back from disk.")]
public sealed class ExportDrawingResult
{
    [Required]
    [Description("The drawing, as it is after the export: same path; its unsaved-changes flag the same unless marked_dirty.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("The format written.")]
    public DrawingExportFormat Format { get; set; }

    [Required]
    [Description("The sheets written, in the drawing's order.")]
    public List<string> Sheets { get; set; } = new List<string>();

    [Required]
    [Description("The files written: one PDF, or one DXF or DWG per sheet.")]
    public List<DrawingExportFile> Files { get; set; } = new List<DrawingExportFile>();

    [Description("True when the export marked a drawing that had no unsaved changes as having them (SOLIDWORKS 2026 SP4 may, and the API cannot clear the flag). It was not saved to clear it; its sheets, views, annotations and tables are measured unchanged.")]
    public bool MarkedDirty { get; set; }

    [Required]
    [Description("The SOLIDWORKS export preferences set for the call and put back afterwards, each as name = value.")]
    public List<string> Preferences { get; set; } = new List<string>();

    [Required]
    [Description("What SOLIDWORKS warned about while exporting (swFileSaveWarning_e names).")]
    public List<string> Warnings { get; set; } = new List<string>();

    [Required]
    [Description("Each check made on the files and the drawing, and what it found.")]
    public List<string> Checks { get; set; } = new List<string>();
}
