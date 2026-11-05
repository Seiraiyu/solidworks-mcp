using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("session.hello", typeof(SessionHelloResult), Internal = true)]
[Description("Opens a connection: exchanges protocol versions and identifies the SOLIDWORKS instance.")]
public sealed class SessionHelloRequest
{
    [Description("The protocol version the client speaks.")]
    public int ProtocolVersion { get; set; }

    [Description("The client's assembly version, recorded in the add-in's log.")]
    public string? ClientVersion { get; set; }
}

[Description("The add-in's side of the hello.")]
public sealed class SessionHelloResult
{
    [Description("The protocol version the add-in speaks.")]
    public int ProtocolVersion { get; set; }

    [Required]
    [Description("The add-in's assembly version.")]
    public string AddinVersion { get; set; } = "";

    [Required]
    [Description("SOLIDWORKS' revision number, for example 34.4.0.")]
    public string SwRevision { get; set; } = "";

    [Description("The process id of sldworks.exe.")]
    public int ProcessId { get; set; }
}

[Operation("session.status", typeof(SessionStatusResult), Internal = true, ReadOnly = true)]
[Description("Reports the SOLIDWORKS session: versions, the active document and how many requests are waiting.")]
public sealed class SessionStatusRequest
{
}

[Description("The state of the SOLIDWORKS session.")]
public sealed class SessionStatusResult
{
    [Required]
    [Description("SOLIDWORKS' revision number, for example 34.4.0.")]
    public string SwRevision { get; set; } = "";

    [Description("The process id of sldworks.exe.")]
    public int ProcessId { get; set; }

    [Required]
    [Description("The add-in's assembly version.")]
    public string AddinVersion { get; set; } = "";

    [Description("The protocol version the add-in speaks.")]
    public int ProtocolVersion { get; set; }

    [Description("The active document, absent when no document is open.")]
    public DocumentInfo? ActiveDocument { get; set; }

    [Description("How many documents are open.")]
    public int OpenDocumentCount { get; set; }

    [Description("Requests waiting behind this one on SOLIDWORKS' UI thread.")]
    public int QueuedRequests { get; set; }
}

[Description("A document open in SOLIDWORKS.")]
public sealed class DocumentInfo
{
    [Required]
    [Description("The document's title as SOLIDWORKS shows it.")]
    public string Title { get; set; } = "";

    [Description("The full path, absent while the document has never been saved.")]
    public string? Path { get; set; }

    [Description("Part, assembly or drawing.")]
    public DocumentType Type { get; set; }

    [Description("True when the document has unsaved changes.")]
    public bool Dirty { get; set; }
}

public enum DocumentType
{
    Part,
    Assembly,
    Drawing,
}
