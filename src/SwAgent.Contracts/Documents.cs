using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("document.new", typeof(DocumentNewResult))]
[Description("Creates a part from the user's default part template (or a named template) and makes it the active document. With path, saves it there at once. The part is the agent's own, so document.close may close it.")]
public sealed class DocumentNewRequest
{
    [Description("What to create; only part in this version. Default part.")]
    public DocumentType? Type { get; set; }

    [Description("Full path of a part template (.PRTDOT); the user's default part template when absent.")]
    public string? Template { get; set; }

    [Description("Full path of a .SLDPRT file that does not exist yet, in an existing folder: the new part is saved there at once.")]
    public string? Path { get; set; }
}

[Description("The part created.")]
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
[Description("Opens a part, assembly or drawing from a local file and makes it the active document. A document that is already open is made active, not opened again. A document this call opened is the agent's own, so document.close may close it.")]
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

    [Description("True when it was already open; it was made active and not opened again.")]
    public bool AlreadyOpen { get; set; }

    [Description("True when it is open read-only.")]
    public bool ReadOnly { get; set; }

    [Required]
    [Description("What SOLIDWORKS warned about while opening it (swFileLoadWarning_e names), for example swFileLoadWarning_ModelOutOfDate.")]
    public List<string> Warnings { get; set; } = new List<string>();
}

[Operation("document.save", typeof(DocumentSaveResult))]
[Description("Saves a document where it already is. A document never saved needs document.save_as. Checked on disk: the file was written by this call.")]
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
}
