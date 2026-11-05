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
