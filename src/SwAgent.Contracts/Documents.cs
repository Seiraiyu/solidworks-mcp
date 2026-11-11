using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("document.new", typeof(DocumentNewResult))]
[Description("Creates a part or an assembly from the user's default template for it (or a named template) and makes it the active document. With path, saves it there at once. The document is the agent's own, so document.close may close it.")]
public sealed class DocumentNewRequest
{
    [Description("What to create: part or assembly (drawings come in a later release). Default part.")]
    public DocumentType? Type { get; set; }

    [Description("Full path of a template: .PRTDOT for a part, .ASMDOT for an assembly; the user's default template for the type when absent.")]
    public string? Template { get; set; }

    [Description("Full path of a file that does not exist yet, in an existing folder, with the type's extension (.SLDPRT, .SLDASM): the new document is saved there at once.")]
    public string? Path { get; set; }
}

[Description("The document created.")]
public sealed class DocumentNewResult
{
    [Required]
    [Description("The new document.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The template it was made from.")]
    public string Template { get; set; } = "";
}

[Operation("document.open", typeof(DocumentOpenResult))]
[Description("Opens a part, assembly or drawing from a local file and makes it the active document. An assembly opens with every component resolved (never lightweight), and never with a dialog. When a component's file is not where the assembly says, SOLIDWORKS looks for the same file name in other folders (ones it opened this session, the assembly's own) and loads what it finds without asking: each such file is listed in substituted, and references issued before to that component's entities no longer resolve (component_file_changed). A file it finds nowhere is listed in missing. A document that is already open is made active, not opened again. A document this call opened is the agent's own, so document.close may close it.")]
public sealed class DocumentOpenRequest
{
    [Required]
    [Description("Full path of an existing .SLDPRT, .SLDASM or .SLDDRW file.")]
    public string Path { get; set; } = "";

    [Description("Open it read-only. Default false.")]
    public bool? ReadOnly { get; set; }
}

[Description("The document opened.")]
public sealed class DocumentOpenResult
{
    [Required]
    [Description("The document.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("True when it was already open in a window; it was made active and not opened again. A document loaded only because an open assembly uses it is opened in a window of its own, and this is false.")]
    public bool AlreadyOpen { get; set; }

    [Description("True when it is open read-only.")]
    public bool ReadOnly { get; set; }

    [Required]
    [Description("What SOLIDWORKS warned about while opening it (swFileLoadWarning_e names), for example swFileLoadWarning_ModelOutOfDate.")]
    public List<string> Warnings { get; set; } = new List<string>();

    [Required]
    [Description("Files the document uses (an assembly's components) that SOLIDWORKS could not find; those components stay suppressed. Empty when nothing is missing.")]
    public List<string> Missing { get; set; } = new List<string>();

    [Required]
    [Description("An assembly's component files SOLIDWORKS loaded from another folder than the assembly says, because the file was not there: check each is the file meant before saving the assembly, which would then point at it. Empty when nothing was substituted.")]
    public List<SubstitutedFile> Substituted { get; set; } = new List<SubstitutedFile>();
}

[Description("A component file SOLIDWORKS loaded in place of the one an assembly names.")]
public sealed class SubstitutedFile
{
    [Required]
    [Description("The full path the assembly stores for it.")]
    public string Expected { get; set; } = "";

    [Required]
    [Description("The full path of the same-named file SOLIDWORKS loaded instead.")]
    public string Loaded { get; set; } = "";
}

[Operation("document.save", typeof(DocumentSaveResult))]
[Description("Saves a document where it already is. A document never saved needs document.save_as. Saving an assembly first saves every document it uses that has unsaved changes (its components, nested ones included), so the saved assembly never points at changes that exist only in memory; each file written is listed in referenced. Checked on disk: every file was written by this call.")]
public sealed class DocumentSaveRequest
{
    [Description("The document: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Operation("document.save_as", typeof(DocumentSaveResult))]
[Description("Saves a document to a new file, which it then is (not a copy). Refuses an existing file unless overwrite. References issued before keep working. Checked on disk: the file was written by this call.")]
public sealed class DocumentSaveAsRequest
{
    [Description("The document: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("Full path of the new file, in an existing folder, with the document's own extension (.SLDPRT for a part).")]
    public string Path { get; set; } = "";

    [Description("Replace the file if it exists. Default false.")]
    public bool? Overwrite { get; set; }
}

[Description("The document saved.")]
public sealed class DocumentSaveResult
{
    [Required]
    [Description("The document, now without unsaved changes.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The file written.")]
    public string Path { get; set; } = "";

    [Description("The file's size in bytes.")]
    public long Bytes { get; set; }

    [Required]
    [Description("What SOLIDWORKS warned about while saving (swFileSaveWarning_e names).")]
    public List<string> Warnings { get; set; } = new List<string>();

    [Required]
    [Description("For document.save of an assembly: the documents it uses that had unsaved changes and were saved first, each where it already was. Empty otherwise.")]
    public List<SavedFile> Referenced { get; set; } = new List<SavedFile>();
}

[Description("A document saved along with the assembly that uses it.")]
public sealed class SavedFile
{
    [Required]
    [Description("The document, now without unsaved changes.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The file written.")]
    public string Path { get; set; } = "";

    [Description("The file's size in bytes.")]
    public long Bytes { get; set; }
}

[Operation("document.close", typeof(DocumentCloseResult))]
[Description("Closes a document. Refuses one with unsaved changes unless discard, and one the agent did not open or create (document.new, document.open) unless users_document, which is only for when the user asked for it to be closed. Closing an assembly also unloads the components no other open document uses (listed in unloaded), so it is refused, unless discard, when any of them has unsaved changes too. A part an open assembly uses only loses its window: it stays loaded for the assembly, with its changes (still_used_by).")]
public sealed class DocumentCloseRequest
{
    [Description("The document: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Close it even though it has unsaved changes, losing them. Default false.")]
    public bool? Discard { get; set; }

    [Description("Close it even though the agent did not open or create it. Only when the user asked for this document to be closed. Default false.")]
    public bool? UsersDocument { get; set; }
}

[Description("The document closed.")]
public sealed class DocumentCloseResult
{
    [Required]
    [Description("The document as it was just before it closed.")]
    public DocumentInfo Closed { get; set; } = new DocumentInfo();

    [Description("True when unsaved changes were lost: its own, or those of a document it unloaded.")]
    public bool Discarded { get; set; }

    [Required]
    [Description("For an assembly: the documents it used that SOLIDWORKS unloaded with it, as they were just before. Empty otherwise.")]
    public List<DocumentInfo> Unloaded { get; set; } = new List<DocumentInfo>();

    [Required]
    [Description("The open documents (assemblies) that still use it: it lost its window but stays loaded for them, unsaved changes included. Empty when it was unloaded.")]
    public List<string> StillUsedBy { get; set; } = new List<string>();
}

[Operation("document.rebuild", typeof(RebuildResult))]
[Description("Rebuilds a part or an assembly (only what changed; everything with force) and reports its rebuild errors and warnings by feature; an assembly's include its mates' errors, each named by the mate. After changing a component's part, rebuild the assembly that uses it.")]
public sealed class DocumentRebuildRequest
{
    [Description("The document: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Rebuild every feature, not only what changed. Default false.")]
    public bool? Force { get; set; }
}

[Operation("model.rebuild_status", typeof(RebuildResult), ReadOnly = true)]
[Description("Reports a part's or an assembly's rebuild errors and warnings by feature (SOLIDWORKS' What's Wrong list; an assembly's include its mates'), without rebuilding it.")]
public sealed class ModelRebuildStatusRequest
{
    [Description("The document: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A document's rebuild errors and warnings.")]
public sealed class RebuildResult
{
    [Required]
    [Description("The document.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Description("How many features have a rebuild error.")]
    public int Errors { get; set; }

    [Description("How many features have a rebuild warning.")]
    public int Warnings { get; set; }

    [Required]
    [Description("Each feature with an error or warning.")]
    public List<RebuildItem> Items { get; set; } = new List<RebuildItem>();
}

[Description("A feature with a rebuild error or warning.")]
public sealed class RebuildItem
{
    [Required]
    [Description("The feature's name.")]
    public string Feature { get; set; } = "";

    [Required]
    [Description("SOLIDWORKS' error code name (swFeatureError_e), for example swFeatureErrorFilletNoEdge.")]
    public string Code { get; set; } = "";

    [Description("True for a warning, false for an error.")]
    public bool Warning { get; set; }
}
